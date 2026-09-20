using nietras.SeparatedValues;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// What the site says right now, fetched at startup. The embedded list is the offline
        /// answer and the state of the world when this build was made; a title promoted since
        /// then is live here without a new build.
        /// </summary>
        private static Dictionary<string, LocaleKeys> _live = new();

        public static (LocaleKeys Status, string Backend)? Find(string titleId)
        {
            bool known = _entries.TryGetValue(titleId, out (LocaleKeys Status, string Backend) entry);

            if (_live.TryGetValue(titleId, out LocaleKeys live))
            {
                return (live, known ? entry.Backend : string.Empty);
            }

            return known ? entry : null;
        }

        /// <summary>
        /// Reads the catalogue's statuses from the site. Failure leaves the embedded list in
        /// place: an emulator that cannot reach the site still knows what it shipped knowing.
        /// </summary>
        /// <returns>true when the site says something this build did not, so a list already
        /// drawn from the embedded answer is now out of date.</returns>
        public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                IReadOnlyDictionary<string, string> statuses =
                    await OpenPakApi.Instance.CatalogueStatusesAsync(cancellationToken);

                Dictionary<string, LocaleKeys> live = new();

                foreach (KeyValuePair<string, string> entry in statuses)
                {
                    LocaleKeys? status = entry.Value switch
                    {
                        "live" => LocaleKeys.Dialog_OpenPak_CompatibilityLive,
                        "beta" => LocaleKeys.Dialog_OpenPak_CompatibilityBeta,
                        "alpha" => LocaleKeys.Dialog_OpenPak_CompatibilityAlpha,
                        _ => null,
                    };

                    if (status.HasValue)
                    {
                        live[entry.Key.ToLowerInvariant()] = status.Value;
                    }
                }

                if (live.Count == 0)
                {
                    Logger.Info?.Print(LogClass.Application,
                        "[OpenPak] Catalogue statuses: the site answered with none, keeping the built-in list");

                    return false;
                }

                bool changed = false;

                foreach (KeyValuePair<string, LocaleKeys> entry in live)
                {
                    if (!_entries.TryGetValue(entry.Key, out (LocaleKeys Status, string Backend) had) ||
                        had.Status != entry.Value)
                    {
                        changed = true;
                        break;
                    }
                }

                _live = live;

                Logger.Info?.Print(LogClass.Application,
                    $"[OpenPak] Catalogue statuses: {live.Count} from the site, {(changed ? "at least one differs from this build" : "all matching this build")}");

                return changed;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"[OpenPak] Catalogue statuses: {exception.Message} — keeping the built-in list");
            }

            return false;
        }

        public static LocaleKeys Tooltip(LocaleKeys status) => status switch
        {
            LocaleKeys.Dialog_OpenPak_CompatibilityLive => LocaleKeys.Dialog_OpenPak_CompatibilityLiveTooltip,
            LocaleKeys.Dialog_OpenPak_CompatibilityBeta => LocaleKeys.Dialog_OpenPak_CompatibilityBetaTooltip,
            _ => LocaleKeys.Dialog_OpenPak_CompatibilityAlphaTooltip,
        };
    }
}
