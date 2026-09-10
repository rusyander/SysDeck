using System;
using System.Globalization;

namespace WpcSetup
{
    // Свой аналог src\Tr: установщик собирается отдельно от приложения и его исходников
    // не видит. Обе строки стоят рядом в одном вызове — так перевод не отстаёт от правки.
    internal static class L
    {
        private static readonly bool RuFlag = DetectRu();

        // Русский — родной язык проекта, поэтому необычный сбой определения культуры
        // трактуется в его пользу.
        private static bool DetectRu()
        {
            try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"; }
            catch { return true; }
        }

        public static bool Ru { get { return RuFlag; } }

        public static string S(string ru, string en) { return RuFlag ? ru : en; }
    }
}
