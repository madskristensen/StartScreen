using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Minimal P/Invoke wrapper around Windows Credential Manager (advapi32).
    /// Used to store/retrieve PATs when Git Credential Manager is unavailable.
    /// </summary>
    internal static class NativeCredentialManager
    {
        public static void Write(string targetName, string username, string password)
        {
            var passwordBytes = Encoding.Unicode.GetBytes(password);

            var credential = new CREDENTIAL
            {
                Type = _credTypeGeneric,
                TargetName = targetName,
                UserName = username,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length),
                Persist = _credPersistLocalMachine,
            };

            try
            {
                Marshal.Copy(passwordBytes, 0, credential.CredentialBlob, passwordBytes.Length);

                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }
        }

        public static (string username, string password)? Read(string targetName)
        {
            if (!CredRead(targetName, _credTypeGeneric, 0, out var credPtr))
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var password = cred.CredentialBlobSize > 0
                    ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2)
                    : string.Empty;

                return (cred.UserName ?? string.Empty, password);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        internal static bool HasGitCredential(string host)
        {
            if (!CredEnumerate("git:*", 0, out var count, out var credentials))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1168)
                    return false;
                throw new Win32Exception(error);
            }
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(credentials, index * IntPtr.Size));
                    if (credential.Type == _credTypeGeneric && IsGitCredentialTargetForHost(credential.TargetName, host))
                        return true;
                }
                return false;
            }
            finally
            {
                CredFree(credentials);
            }
        }

        internal static bool IsGitCredentialTargetForHost(string target, string host)
        {
            return target?.StartsWith("git:", StringComparison.OrdinalIgnoreCase) == true
                && Uri.TryCreate(target.Substring(4), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);
        }

        public static void Delete(string targetName)
        {
            CredDelete(targetName, _credTypeGeneric, 0);
        }

        private const int _credTypeGeneric = 1;
        private const int _credPersistLocalMachine = 2;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string targetName, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredDelete(string targetName, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }
    }
}
