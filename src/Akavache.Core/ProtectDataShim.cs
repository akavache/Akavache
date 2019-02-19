﻿namespace Akavache
{
    public static class ProtectedData
    {
        public static byte[] Protect(byte[] originalData, byte[] entropy, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            return originalData;
        }

        public static byte[] Unprotect(byte[] originalData, byte[] entropy, DataProtectionScope scope = DataProtectionScope.CurrentUser)
        {
            return originalData;
        }
    }

    public enum DataProtectionScope {
        CurrentUser,
    }
}