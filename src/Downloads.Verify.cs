// SysDeck — «Загрузки»: проверка скачанного — подпись Authenticode, метка «из интернета».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Подпись проверяется WinVerifyTrust без интерфейса и без похода в сеть за списками отзыва: карточка открывается мгновенно
// и офлайн. Скачанные файлы подписываются встроенной подписью; каталоги Windows подписывают только системные файлы.
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SysDeck.Downloads
{
    internal enum DlSignature { Valid, Unsigned, Invalid, Untrusted, Error }

    internal sealed class DlSignatureInfo
    {
        public DlSignature Kind;
        public string Publisher = "";
        public string Detail = "";
    }

    internal static class DlVerify
    {
        private const uint TrustENoSignature = 0x800B0100;
        private const uint TrustESubjectFormUnknown = 0x800B0003;
        private const uint TrustEProviderUnknown = 0x800B0001;
        private const uint TrustEBadDigest = 0x80096010;
        private const uint TrustEExplicitDistrust = 0x800B0111;
        private const uint TrustESubjectNotTrusted = 0x800B0004;
        private const uint CertEUntrustedRoot = 0x800B0109;
        private const uint CertEExpired = 0x800B0101;
        private const uint CertERevoked = 0x800B010C;
        private const uint CertEChaining = 0x800B010A;
        private const uint CryptESecuritySettings = 0x80092026;
        private const uint CryptEFileError = 0x80092003;

        private static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);

        private const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1, WtdStateActionIgnore = 0;
        private const uint WtdCacheOnlyUrlRetrieval = 0x1000, WtdRevocationCheckNone = 0x10;

        // Результат для файла на диске. Исключения не бросает: всё непонятное — Error с текстом.
        public static DlSignatureInfo Check(string path)
        {
            DlSignatureInfo info = new DlSignatureInfo();
            uint hr;
            IntPtr filePtr = IntPtr.Zero;
            try
            {
                WintrustFileInfo file = new WintrustFileInfo();
                file.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo));
                file.pcwszFilePath = path;
                filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
                Marshal.StructureToPtr(file, filePtr, false);
                WintrustData data = new WintrustData();
                data.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData));
                data.dwUIChoice = WtdUiNone;
                data.fdwRevocationChecks = WtdRevokeNone;
                data.dwUnionChoice = WtdChoiceFile;
                data.pFile = filePtr;
                data.dwStateAction = WtdStateActionIgnore;
                data.dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone;
                hr = WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, ref data);
            }
            catch (Exception ex)
            {
                info.Kind = DlSignature.Error;
                info.Detail = ex.Message;
                return info;
            }
            finally
            {
                if (filePtr != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(filePtr, typeof(WintrustFileInfo));
                    Marshal.FreeHGlobal(filePtr);
                }
            }

            switch (hr)
            {
                case 0:
                    info.Kind = DlSignature.Valid;
                    break;
                case TrustENoSignature:
                case TrustESubjectFormUnknown:
                case TrustEProviderUnknown:
                    info.Kind = DlSignature.Unsigned;
                    break;
                case TrustEBadDigest:
                    info.Kind = DlSignature.Invalid;
                    info.Detail = Tr.S("файл изменён после подписи", "the file was changed after signing");
                    break;
                case TrustEExplicitDistrust:
                case CertERevoked:
                    info.Kind = DlSignature.Invalid;
                    info.Detail = Tr.S("сертификат отозван или запрещён", "the certificate is revoked or distrusted");
                    break;
                case CertEUntrustedRoot:
                case CertEChaining:
                case TrustESubjectNotTrusted:
                    info.Kind = DlSignature.Untrusted;
                    info.Detail = Tr.S("издатель не проверен: цепочка сертификатов не доверена", "publisher not verified: the certificate chain is not trusted");
                    break;
                case CertEExpired:
                    info.Kind = DlSignature.Untrusted;
                    info.Detail = Tr.S("срок сертификата истёк, а метки времени нет", "the certificate expired and there is no timestamp");
                    break;
                case CryptESecuritySettings:
                    info.Kind = DlSignature.Untrusted;
                    info.Detail = Tr.S("проверка запрещена политикой", "verification is disabled by policy");
                    break;
                case CryptEFileError:
                    info.Kind = DlSignature.Error;
                    info.Detail = Tr.S("файл не прочитан", "the file could not be read");
                    break;
                default:
                    info.Kind = DlSignature.Error;
                    info.Detail = "0x" + hr.ToString("X8");
                    break;
            }
            if (info.Kind != DlSignature.Unsigned && info.Kind != DlSignature.Error) info.Publisher = Publisher(path);
            return info;
        }

        // CN сертификата подписи; пусто — не прочитан.
        public static string Publisher(string path)
        {
            try
            {
                using (X509Certificate2 cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    string name = cert.GetNameInfo(X509NameType.SimpleName, false);
                    return name ?? "";
                }
            }
            catch { return ""; }
        }

        public static string Describe(DlSignatureInfo s)
        {
            if (s == null) return "";
            string who = s.Publisher.Length > 0 ? s.Publisher : Tr.S("издатель не прочитан", "publisher not read");
            switch (s.Kind)
            {
                case DlSignature.Valid: return Tr.S("✓ подписан: ", "✓ signed: ") + who;
                case DlSignature.Unsigned: return Tr.S("⚠ без цифровой подписи", "⚠ not digitally signed");
                case DlSignature.Invalid: return Tr.S("✕ подпись недействительна: ", "✕ invalid signature: ") + s.Detail + (s.Publisher.Length > 0 ? " (" + s.Publisher + ")" : "");
                case DlSignature.Untrusted: return Tr.S("⚠ подпись есть, но ", "⚠ signed, but ") + s.Detail + (s.Publisher.Length > 0 ? " (" + s.Publisher + ")" : "");
                default: return Tr.S("подпись не проверена: ", "signature not checked: ") + s.Detail;
            }
        }

        // «интернет» / «локальная сеть» / … из потока Zone.Identifier; пусто — метки нет.
        public static string ZoneText(string path)
        {
            string text = DlMotw.ReadZoneStream(path);
            if (string.IsNullOrEmpty(text)) return "";
            int zone = -1;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(7).Trim(), out zone);
            }
            switch (zone)
            {
                case 0: return Tr.S("этот компьютер", "this computer");
                case 1: return Tr.S("локальная сеть", "local intranet");
                case 2: return Tr.S("надёжные сайты", "trusted sites");
                case 3: return Tr.S("интернет — Windows спросит перед запуском", "internet — Windows asks before running");
                case 4: return Tr.S("опасные сайты", "restricted sites");
                default: return Tr.S("метка есть, зона не разобрана", "marked, zone not recognised");
            }
        }
    }
}
