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
    ///
    /// The same catalogue also carries the playability verdict the three emulators share — see
    /// <see cref="Playability"/> — which is a different axis: how far the game runs here, not how
    /// far OpenPak serves it online.
    /// </summary>
    public static class OpenPakCompatibility
    {
        /// <summary>
        /// How far each catalogue title runs in *this* emulator, the website's answer rather than
        /// Ryujinx's own. One row on the site is what all three emulators read, so a verdict is
        /// entered once. Filled by <see cref="Load"/>, hence declared before <see cref="_entries"/>:
        /// static initialisers run in declaration order.
        /// </summary>
        private static readonly Dictionary<string, LocaleKeys> _playability = new();

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

                string titleId = row["title_id"].ToString().ToLowerInvariant();

                if (status.HasValue)
                    entries[titleId] = (status.Value, row["backend"].ToString().Trim('"'));

                if (PlayabilityKey(row["playability"].ToString()) is { } playability)
                    _playability[titleId] = playability;
            }

            return entries;
        }

        /// <summary>
        /// The website's five playability words onto the labels the game list already draws. They
        /// are the same words docs/compatibility.csv uses in its status column, so this is the
        /// same vocabulary, not a second one. Anything else is the site having no opinion.
        /// </summary>
        private static LocaleKeys? PlayabilityKey(string value) => value switch
        {
            "playable" => LocaleKeys.CompatibilityListPlayable,
            "ingame" => LocaleKeys.CompatibilityListIngame,
            "menus" => LocaleKeys.CompatibilityListMenus,
            "boots" => LocaleKeys.CompatibilityListBoots,
            "nothing" => LocaleKeys.CompatibilityListNothing,
            _ => null,
        };

        /// <summary>What the site says this title's playability is, or null when it says nothing.</summary>
        public static LocaleKeys? Playability(string titleId)
        {
            if (_livePlayability.TryGetValue(titleId, out LocaleKeys live))
                return live;

            return _playability.TryGetValue(titleId, out LocaleKeys had) ? had : null;
        }

        /// <summary>
        /// What the site says right now, fetched at startup. The embedded list is the offline
        /// answer and the state of the world when this build was made; a title promoted since
        /// then is live here without a new build.
        /// </summary>
        private static Dictionary<string, LocaleKeys> _live = new();

        /// <summary>The same live-over-embedded layering for the playability verdict.</summary>
        private static Dictionary<string, LocaleKeys> _livePlayability = new();

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

                // The playability verdict rides the same fetch. It is independent of the online
                // status — a title can be unserved and still known to run — so it is applied even
                // when no status came back, and an id the site is silent about stays absent here
                // so the caller falls back to Ryujinx's own compatibility row.
                Dictionary<string, LocaleKeys> livePlayability = new();

                foreach (KeyValuePair<string, string> entry in
                    await OpenPakApi.Instance.CataloguePlayabilityAsync(cancellationToken))
                {
                    if (PlayabilityKey(entry.Value) is { } key)
                    {
                        livePlayability[entry.Key.ToLowerInvariant()] = key;
                    }
                }

                // Compared against the answer in force right now — the embedded verdict on the
                // first refresh — so a site that merely agrees with this build does not redraw the
                // whole game list at every launch.
                // ponytail: a verdict *withdrawn* since the last refresh is not noticed. RefreshAsync
                // runs once per launch, so there is no last refresh; if it ever runs on a timer,
                // also compare _livePlayability's keys against the new map.
                bool playabilityChanged = false;

                foreach (KeyValuePair<string, LocaleKeys> entry in livePlayability)
                {
                    if (Playability(entry.Key) != entry.Value)
                    {
                        playabilityChanged = true;
                        break;
                    }
                }

                _livePlayability = livePlayability;

                if (live.Count == 0)
                {
                    Logger.Info?.Print(LogClass.Application,
                        "[OpenPak] Catalogue statuses: the site answered with none, keeping the built-in list");

                    return playabilityChanged;
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
                    $"[OpenPak] Catalogue statuses: {live.Count} from the site, {(changed ? "at least one differs from this build" : "all matching this build")}"
                    + $"; {livePlayability.Count} playability verdict(s){(playabilityChanged ? ", at least one new" : "")}");

                return changed || playabilityChanged;
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
