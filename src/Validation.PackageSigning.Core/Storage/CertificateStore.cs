﻿using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace NuGet.Jobs.Validation.PackageSigning.Storage
{
    public class CertificateStore : ICertificateStore
    {
        public Task<bool> Exists(string thumbprint)
        {
            throw new NotImplementedException();
        }

        public Task<X509Certificate2> Load(string thumbprint)
        {
            throw new NotImplementedException();
        }

        public Task Save(X509Certificate2 certificate)
        {
            throw new NotImplementedException();
        }
    }
}
