using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The OS's own password store, and nothing else.
    ///
    /// The bearer this holds is the account: whoever has it is signed in as that person until it
    /// is revoked. A config file next to the settings would be readable by anything that can read
    /// the settings — including a crash report, a backup, or a pasted log — so there is no file
    /// fallback here at all. When no backend answers, <see cref="Available"/> is false and the
    /// sign-in refuses loudly rather than quietly writing the token somewhere it should not be.
    /// </summary>
    public static partial class SecretStore
    {
        private const string Service = "org.openpak.ryujinx";

        private static bool? _available;
        private static string _reason;

        /// <summary>Whether a password store answered. False means sign-in is not possible here.</summary>
        public static bool Available
        {
            get
            {
                _available ??= Probe();

                return _available.Value;
            }
        }

        /// <summary>Why there is no store, in words a person can act on. Null when there is one.</summary>
        public static string UnavailableReason => Available ? null : _reason;

        /// <summary>The secret kept under <paramref name="key"/>, or null when there is none.</summary>
        public static string Load(string key)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return WindowsCredential.Load(Target(key));
                }

                if (OperatingSystem.IsMacOS())
                {
                    return Run("security", ["find-generic-password", "-s", Service, "-a", key, "-w"], null, out string found)
                        ? found.TrimEnd('\n')
                        : null;
                }

                return Run("secret-tool", ["lookup", "service", Service, "account", key], null, out string secret) && secret.Length > 0
                    ? secret
                    : null;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not read the saved sign-in: {exception.Message}");

                return null;
            }
        }

        /// <summary>Keep <paramref name="secret"/> under <paramref name="key"/>. False if it could not be kept.</summary>
        public static bool Store(string key, string secret)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return WindowsCredential.Store(Target(key), key, secret);
                }

                if (OperatingSystem.IsMacOS())
                {
                    // -U updates an existing item rather than failing. The secret is an argument
                    // here because `security` has no way to take one on stdin; on macOS another
                    // user cannot read this process's arguments, and the same user could read the
                    // keychain item anyway.
                    return Run("security",
                        ["add-generic-password", "-U", "-s", Service, "-a", key, "-w", secret], null, out _);
                }

                // Through stdin, so the token never appears in anyone's process list.
                return Run("secret-tool",
                    ["store", "--label", "OpenPak (Ryujinx)", "service", Service, "account", key], secret, out _);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not save the sign-in: {exception.Message}");

                return false;
            }
        }

        /// <summary>Forget the secret under <paramref name="key"/>. Missing is not a failure.</summary>
        public static void Erase(string key)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    WindowsCredential.Erase(Target(key));
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Run("security", ["delete-generic-password", "-s", Service, "-a", key], null, out _);
                }
                else
                {
                    Run("secret-tool", ["clear", "service", Service, "account", key], null, out _);
                }
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] Could not forget the sign-in: {exception.Message}");
            }
        }

        private static string Target(string key) => $"{Service}:{key}";

        private static bool Probe()
        {
            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (Run("security", ["-h"], null, out _))
                {
                    return true;
                }

                _reason = "the macOS keychain could not be reached (`security` did not answer).";

                return false;
            }

            // `lookup` on a name nothing has stored exits non-zero, so the question is only
            // whether the tool and a keyring are there to answer at all.
            try
            {
                using Process probe = Start("secret-tool", ["lookup", "service", Service, "account", "probe"], false);

                probe.WaitForExit(5000);
            }
            catch (Exception)
            {
                _reason = "no password store answered. Install libsecret (`secret-tool`) and unlock a keyring, " +
                    "then sign in again — OpenPak will not write an account token to a plain file.";

                return false;
            }

            return true;
        }

        private static Process Start(string file, string[] arguments, bool redirectInput)
        {
            ProcessStartInfo info = new()
            {
                FileName = file,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            return Process.Start(info);
        }

        private static bool Run(string file, string[] arguments, string input, out string output)
        {
            output = string.Empty;

            using Process process = Start(file, arguments, input != null);

            if (process == null)
            {
                return false;
            }

            if (input != null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            output = process.StandardOutput.ReadToEnd();

            process.WaitForExit(10000);

            return process.HasExited && process.ExitCode == 0;
        }

        /// <summary>Windows Credential Manager, which is always there and needs no helper process.</summary>
        private static partial class WindowsCredential
        {
            private const int GenericCredential = 1;
            private const int PersistLocalMachine = 2;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct Credential
            {
                public int Flags;
                public int Type;
                public IntPtr TargetName;
                public IntPtr Comment;
                public long LastWritten;
                public int CredentialBlobSize;
                public IntPtr CredentialBlob;
                public int Persist;
                public int AttributeCount;
                public IntPtr Attributes;
                public IntPtr TargetAlias;
                public IntPtr UserName;
            }

            [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

            [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool CredWrite(ref Credential credential, int flags);

            [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool CredDelete(string target, int type, int flags);

            [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
            private static partial void CredFree(IntPtr buffer);

            public static string Load(string target)
            {
                if (!CredRead(target, GenericCredential, 0, out IntPtr handle))
                {
                    return null;
                }

                try
                {
                    Credential credential = Marshal.PtrToStructure<Credential>(handle);

                    return credential.CredentialBlobSize == 0
                        ? null
                        : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
                }
                finally
                {
                    CredFree(handle);
                }
            }

            public static bool Store(string target, string user, string secret)
            {
                IntPtr blob = Marshal.StringToCoTaskMemUni(secret);
                IntPtr targetName = Marshal.StringToCoTaskMemUni(target);
                IntPtr userName = Marshal.StringToCoTaskMemUni(user);

                try
                {
                    Credential credential = new()
                    {
                        Type = GenericCredential,
                        TargetName = targetName,
                        CredentialBlob = blob,
                        CredentialBlobSize = Encoding.Unicode.GetByteCount(secret),
                        Persist = PersistLocalMachine,
                        UserName = userName,
                    };

                    return CredWrite(ref credential, 0);
                }
                finally
                {
                    Marshal.ZeroFreeCoTaskMemUnicode(blob);
                    Marshal.FreeCoTaskMem(targetName);
                    Marshal.FreeCoTaskMem(userName);
                }
            }

            public static void Erase(string target) => CredDelete(target, GenericCredential, 0);
        }
    }
}
