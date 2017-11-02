﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Text;

namespace NuGet.Packaging.Signing
{
    public static class KeyPairFileUtility
    {
        /// <summary>
        /// Max file size.
        /// </summary>
        public const int MaxSize = 1024 * 1024;

        /// <summary>
        /// File encoding.
        /// </summary>
        public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Throw if the expected value does not exist.
        /// </summary>
        public static string GetValueOrThrow(Dictionary<string, string> values, string key)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value;
            }

            throw new SignatureException($"Missing expected key: {key}");
        }
    }
}