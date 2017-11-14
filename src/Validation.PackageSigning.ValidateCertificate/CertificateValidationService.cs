﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Jobs.Validation.PackageSigning.Messages;
using NuGet.Services.Validation;

namespace Validation.PackageSigning.ValidateCertificate
{
    public class CertificateValidationService : ICertificateValidationService
    {
        private const int MaxSignatureUpdatesPerTransaction = 500;

        private readonly IValidationEntitiesContext _context;
        private readonly IAlertingService _alertingService;
        private readonly ILogger<CertificateValidationService> _logger;
        private readonly int _maximumValidationFailures;

        public CertificateValidationService(
            IValidationEntitiesContext context,
            IAlertingService alertingService,
            ILogger<CertificateValidationService> logger,
            int maximumValidationFailures)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _alertingService = alertingService ?? throw new ArgumentNullException(nameof(alertingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _maximumValidationFailures = maximumValidationFailures;
        }

        public Task<CertificateValidation> FindCertificateValidationAsync(CertificateValidationMessage message)
        {
            return _context
                        .CertificateValidations
                        .Where(v => v.ValidationId == message.ValidationId && v.CertificateKey == message.CertificateKey)
                        .Include(v => v.Certificate)
                        .FirstOrDefaultAsync();
        }

        public Task<CertificateVerificationResult> VerifyAsync(X509Certificate2 certificate)
        {
            // TODO: This will be implemented in a separate change!
            throw new NotImplementedException();
        }

        public async Task<bool> TrySaveResultAsync(CertificateValidation validation, CertificateVerificationResult result)
        {
            if (validation.Certificate.Status == CertificateStatus.Revoked && result.Status != CertificateStatus.Revoked)
            {
                _logger.LogWarning(
                    "Updating previously revoked certificate {CertificateThumbprint} to status {NewStatus}",
                    validation.Certificate.Thumbprint,
                    result.Status);
            }

            try
            {
                switch (result.Status)
                {
                    case CertificateStatus.Good:
                        await SaveGoodCertificateStatus(validation);
                        break;

                    case CertificateStatus.Unknown:
                        await SaveUnknownCertificateStatus(validation);
                        break;

                    case CertificateStatus.Invalid:
                        await SaveInvalidCertificateStatusAsync(validation);
                        break;

                    case CertificateStatus.Revoked:
                        await SaveRevokedCertificateStatusAsync(validation, result.RevocationTime.Value);
                        break;

                    default:
                        _logger.LogError(
                            $"Unknown {nameof(CertificateStatus)} value: {{CertificateStatus}}, throwing to retry",
                            result.Status);

                        throw new InvalidOperationException($"Unknown {nameof(CertificateStatus)} value: {result.Status}");
                }

                return true;
            }
            catch (DbUpdateConcurrencyException e)
            {
                // The update concurrency exception be triggered by either the Certificate record or one of the dependent
                // PackageSignature records. Regardless, retry the validation so that the Certificate is validated with
                // the new state.
                _logger.LogWarning(
                    0,
                    e,
                    "Failed to update certificate {CertificateThumbprint} to status {NewStatus} due to concurrency exception",
                    validation.Certificate.Thumbprint,
                    result.Status);

                return false;
            }
        }

        private Task SaveGoodCertificateStatus(CertificateValidation validation)
        {
            // TODO: StatusUpdateTime and NextStatusUpdateTime!
            validation.Certificate.Status = CertificateStatus.Good;
            validation.Certificate.StatusUpdateTime = null;
            validation.Certificate.NextStatusUpdateTime = null;
            validation.Certificate.LastVerificationTime = DateTime.UtcNow;
            validation.Certificate.RevocationTime = null;
            validation.Certificate.ValidationFailures = 0;

            validation.Status = CertificateStatus.Good;

            return _context.SaveChangesAsync();
        }

        private Task SaveUnknownCertificateStatus(CertificateValidation validation)
        {
            validation.Certificate.ValidationFailures++;

            if (validation.Certificate.ValidationFailures >= _maximumValidationFailures)
            {
                // The maximum number of validation failures has been reached. The certificate's
                // validation should not be retried as a NuGet Admin will need to investigate the issues.
                // If the certificate is found to be invalid, the Admin will need to invalidate packages
                // and timestamps that depend on this certificate!
                validation.Certificate.Status = CertificateStatus.Invalid;
                validation.Certificate.LastVerificationTime = DateTime.UtcNow;

                validation.Status = CertificateStatus.Invalid;

                _logger.LogWarning(
                    "Certificate {CertificateThumbprint} has reached maximum of {MaximumValidationFailures} failed validation attempts " +
                    "and requires manual investigation by NuGet Admin. Firing alert...",
                    validation.Certificate.Thumbprint,
                    _maximumValidationFailures);

                _alertingService.FireUnableToValidateCertificateAlert(validation.Certificate);
            }

            return _context.SaveChangesAsync();
        }

        private Task SaveInvalidCertificateStatusAsync(CertificateValidation validation)
        {
            void InvalidateSignature(PackageSignature signature)
            {
                if (signature.Status != PackageSignatureStatus.InGracePeriod)
                {
                    _logger.LogWarning(
                        "Signature {SignatureKey} SHOULD be invalidated by NuGet Admin due to invalid certificate {CertificateThumbprint}. Firing alert...",
                        signature.Key,
                        validation.Certificate.Thumbprint);

                    _alertingService.FirePackageSignatureShouldBeInvalidatedAlert(signature);
                }

                signature.Status = PackageSignatureStatus.Invalid;
                signature.PackageSigningState.SigningStatus = PackageSigningStatus.Invalid;
            }

            void InvalidateCertificate()
            {
                // TODO: StatusUpdateTime and NextStatusUpdateTime!
                validation.Certificate.Status = CertificateStatus.Invalid;
                validation.Certificate.StatusUpdateTime = null;
                validation.Certificate.NextStatusUpdateTime = null;
                validation.Certificate.LastVerificationTime = DateTime.UtcNow;
                validation.Certificate.RevocationTime = null;
                validation.Certificate.ValidationFailures = 0;

                validation.Status = CertificateStatus.Invalid;
            }

            return InvalidateDependentSignatures(
                        validation.Certificate,
                        InvalidateSignature,
                        onAllSignaturesInvalidated: InvalidateCertificate);
        }

        private Task SaveRevokedCertificateStatusAsync(CertificateValidation validation, DateTime revocationTime)
        {
            void InvalidateSignature(PackageSignature signature)
            {
                // A revoked certificate does not necessarily invalidate a dependent signature. Skip signatures
                // that should NOT be invalidated.
                if (!RevokedCertificateInvalidatesSignature(validation.Certificate, signature, revocationTime))
                {
                    return;
                }

                if (signature.Status != PackageSignatureStatus.InGracePeriod)
                {
                    _logger.LogWarning(
                        "Signature {SignatureKey} SHOULD be invalidated by NuGet Admin due to revoked certificate {CertificateThumbprint}. Firing alert...",
                        signature.Key,
                        validation.Certificate.Thumbprint);

                    _alertingService.FirePackageSignatureShouldBeInvalidatedAlert(signature);
                }

                signature.Status = PackageSignatureStatus.Invalid;
                signature.PackageSigningState.SigningStatus = PackageSigningStatus.Invalid;
            }

            void RevokeCertificate()
            {
                validation.Certificate.Status = CertificateStatus.Revoked;
                validation.Certificate.StatusUpdateTime = null;
                validation.Certificate.NextStatusUpdateTime = null;
                validation.Certificate.LastVerificationTime = DateTime.UtcNow;
                validation.Certificate.RevocationTime = revocationTime.ToUniversalTime();
                validation.Certificate.ValidationFailures = 0;

                validation.Status = CertificateStatus.Revoked;
            }

            return InvalidateDependentSignatures(
                        validation.Certificate,
                        InvalidateSignature,
                        onAllSignaturesInvalidated: RevokeCertificate);
        }

        /// <summary>
        /// Determines whether a certificate that will be revoked will invalidate the signature.
        /// </summary>
        /// <param name="certificate">The certificate that will be revoked.</param>
        /// <param name="signature">The signature that may be invalidated.</param>
        /// <param name="revocationTime">The time at which the certificate was revoked.</param>
        /// <returns>Whether the signature should be invalidated.</returns>
        private bool RevokedCertificateInvalidatesSignature(
            Certificate certificate,
            PackageSignature signature,
            DateTime revocationTime)
        {
            // The signature may depend on a certificate in one of two ways: either the signature itself was signed with
            // the certificate, or, the trusted timestamp authority used the certificate to sign its timestamp. Note that
            // it is "possible" that both the signature and the trusted timestamp depend on the certificate.
            if (signature.Certificate.Thumbprint == certificate.Thumbprint)
            {
                // The signature was signed using the certificate. Ensure that none of the trusted timestamps indicate
                // that the signature was created after the certificate's invalidity date begins.
                if (!signature.TrustedTimestamps.Any() ||
                    signature.TrustedTimestamps.Any(t => revocationTime <= t.Value))
                {
                    return true;
                }
            }

            // If any of the signature's trusted timestamps depend on the revoked certificate,
            // the signature should be revoked.
            return signature.TrustedTimestamps.Any(t => t.Certificate == certificate);
        }

        /// <summary>
        /// The helper method used to invalidate the signatures that depend on the given certificate.
        /// </summary>
        /// <param name="certificate">The certificate whose dependent signatures should be invalidated.</param>
        /// <param name="invalidateSignature">The action called to invalidate a dependent signature.</param>
        /// <param name="onAllSignaturesInvalidated">The action that will be called once all dependent signatures have been invalidated.</param>
        /// <returns></returns>
        public async Task InvalidateDependentSignatures(
            Certificate certificate,
            Action<PackageSignature> invalidateSignature,
            Action onAllSignaturesInvalidated)
        {
            // A single certificate may be dependend on by many signatures. To ensure sanity, only up
            // to "MaxSignatureUpdatesPerTransaction" signatures will be invalidated at a time.
            List<PackageSignature> signatures = null;
            int page = 0;

            do
            {
                // If necessary, save the previous iteration's signature invalidations.
                if (page > 0)
                {
                    _logger.LogInformation(
                        "Persisting {Signatures} signature invalidations for certificate {CertificateThumbprint} (page {Page})",
                        signatures.Count,
                        certificate.Thumbprint,
                        page);

                    await _context.SaveChangesAsync();
                    page++;
                }

                _logger.LogInformation(
                    "Finding more signatures to invalidate for certificate {CertificateThumbprint}... (page {Page})",
                    certificate.Thumbprint,
                    page);

                signatures = await FindSignatures(certificate, page);

                _logger.LogInformation(
                    "Invalidating {Signatures} signatures for certificate {CertificateThumbprint}... (page {Page})",
                    signatures.Count,
                    certificate.Thumbprint,
                    page);

                foreach (var signature in signatures)
                {
                    invalidateSignature(signature);
                }
            }
            while (signatures.Count == MaxSignatureUpdatesPerTransaction);

            // All signatures have been invalidated. Do any necessary finalizations, and persist the results.
            _logger.LogInformation(
                "Finalizing {Signatures} signature invalidations for certificate {CertificateThumbprint} (total pages: {Pages})",
                signatures.Count,
                certificate.Thumbprint,
                page + 1);

            onAllSignaturesInvalidated();

            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Find all package signatures that depend on the given certificate. This method will return signatures in
        /// batches of size <see cref="MaxSignatureUpdatesPerTransaction"/>.
        /// </summary>
        /// <param name="certificate">The certificate whose signatures should be found.</param>
        /// <param name="page">Which page of signatures should be fetched.</param>
        /// <returns>The signatures that depend on the given certificate.</returns>
        private Task<List<PackageSignature>> FindSignatures(Certificate certificate, int page)
        {
            // A signature may depend on a certificate in one of two ways: the signature itself may have been signed using
            // the certificate, or, one of the signature's trusted timestamps may have been signed using the certificate.
            return _context
                        .PackageSignatures
                        .Where(s =>
                            s.Certificate.Thumbprint == certificate.Thumbprint ||
                            s.TrustedTimestamps.Any(t => t.Certificate.Thumbprint == certificate.Thumbprint))
                        .Include(s => s.TrustedTimestamps)
                        .Include(s => s.PackageSigningState)
                        .OrderBy(s => s.Key)
                        .Skip(page * MaxSignatureUpdatesPerTransaction)
                        .Take(MaxSignatureUpdatesPerTransaction)
                        .ToListAsync();
        }
    }
}
