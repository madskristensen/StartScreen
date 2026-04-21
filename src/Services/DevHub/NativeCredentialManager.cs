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
                Type = CRED_TYPE_GENERIC,
                TargetName = targetName,
                UserName = username,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
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
            if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credPtr))
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

        public static void Delete(string targetName)
        {
            CredDelete(targetName, CRED_TYPE_GENERIC, 0);
        }

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string targetName, int type, int flags, out IntPtr credential);

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
