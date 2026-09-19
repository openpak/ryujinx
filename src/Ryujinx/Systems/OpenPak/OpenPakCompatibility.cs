using nietras.SeparatedValues;
using Ryujinx.Ava.Common.Locale;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Ryujinx.Ava.Systems.OpenPak
{
    /// <summary>
    /// How far OpenPak serves a title's online play, shown beside Ryujinx's own playability.
    /// docs/openpak-compatibility.csv is generated from the website's catalogue
    /// (https://openpak.org/api/v1/catalog.json, the source of truth) by
    /// scripts/update-openpak-compatibility.py: its Switch titles at live, beta or alpha.
    /// </summary>
    public static class OpenPakCompatibility
    {
        private static readonly Dictionary<string, (LocaleKeys Status, string Backend)> _entries = Load();

        private static Dictionary<string, (LocaleKeys, string)> Load()
        {
            using Stream csvStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenPakGameCompatibilityList")!;
            using SepReader reader = Sep.Reader().From(csvStream);

            Dictionary<string, (LocaleKeys, string)> entries = new();

            foreach (SepReader.Row row in reader)
            {
                LocaleKeys? status = row["status"].ToString() switch
                {
                    "live" => LocaleKeys.Dialog_OpenPak_CompatibilityLive,
                    "beta" => LocaleKeys.Dialog_OpenPak_CompatibilityBeta,
                    "alpha" => LocaleKeys.Dialog_OpenPak_CompatibilityAlpha,
                    _ => null,
                };

                if (status.HasValue)
                    entries[row["title_id"].ToString().ToLowerInvariant()] = (status.Value, row["backend"].ToString().Trim('"'));
            }

            return entries;
        }

        public static (LocaleKeys Status, string Backend)? Find(string titleId)
            => _entries.TryGetValue(titleId, out (LocaleKeys, string) entry) ? entry : null;

        public static LocaleKeys Tooltip(LocaleKeys status) => status switch
        {
            LocaleKeys.Dialog_OpenPak_CompatibilityLive => LocaleKeys.Dialog_OpenPak_CompatibilityLiveTooltip,
            LocaleKeys.Dialog_OpenPak_CompatibilityBeta => LocaleKeys.Dialog_OpenPak_CompatibilityBetaTooltip,
            _ => LocaleKeys.Dialog_OpenPak_CompatibilityAlphaTooltip,
        };
    }
}
