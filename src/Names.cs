using System.Collections.Generic;
using System.Text;

namespace RandomDelivery
{
    /// <summary>
    /// Lenient name matching so users can write friendly config names ("KidnapperFox", "TulipSnake")
    /// and still match the game's internal enemy/prefab names ("BushWolf", "FlowerSnake").
    /// Ported from the sibling LethalPresents mod, which proved the aliases against v81.
    /// </summary>
    internal static class Names
    {
        /// <summary>Lowercase, letters+digits only – strips spaces, punctuation and case.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static HashSet<string> NormalizedSet(IEnumerable<string> raw)
        {
            var set = new HashSet<string>();
            if (raw == null) return set;
            foreach (var r in raw)
            {
                string n = Normalize(r);
                if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        /// <summary>Maps a friendly (normalized) monster name to possible internal name fragments.</summary>
        public static IEnumerable<string> MonsterAliases(string normalized)
        {
            yield return normalized;
            switch (normalized)
            {
                case "manticoil": yield return "doublewing"; break;
                case "roaminglocust":
                case "redlocust":
                    yield return "redlocustbees"; yield return "docilelocust"; yield return "locust"; break;
                case "hoardingbug": yield return "hoarderbug"; yield return "hoarding"; break;
                case "gunkfish":
                case "stingray":
                case "backwatergunkfish":
                    // The Backwater Gunkfish is known internally as "Stingray" (verified on the wiki).
                    yield return "stingray"; yield return "gunkfish"; yield return "backwatergunkfish";
                    yield return "backwater"; yield return "fish"; break;
                case "slime": yield return "hygrodere"; yield return "blob"; break;
                case "tulipsnake": yield return "flowersnake"; yield return "snake"; break;
                case "maneater": yield return "cavedweller"; yield return "caveddweller"; break;
            }
        }

        /// <summary>
        /// True if any of an entity's candidate names (display / prefab / asset) matches any token in
        /// <paramref name="set"/>, expanding each token through <see cref="MonsterAliases"/>.
        /// </summary>
        public static bool MonsterMatchesSet(IEnumerable<string> candidatesRaw, HashSet<string> set)
        {
            if (set == null || set.Count == 0) return false;

            var candidates = new List<string>();
            foreach (var c in candidatesRaw)
            {
                string n = Normalize(c);
                if (n.Length > 0) candidates.Add(n);
            }

            foreach (string token in set)
                foreach (string alias in MonsterAliases(token))
                {
                    if (alias.Length == 0) continue;
                    foreach (string cand in candidates)
                        if (cand == alias || cand.Contains(alias) || alias.Contains(cand))
                            return true;
                }
            return false;
        }

        /// <summary>Plain normalized containment match (used for items and traps by name).</summary>
        public static bool NameMatchesSet(IEnumerable<string> candidatesRaw, HashSet<string> set)
        {
            if (set == null || set.Count == 0) return false;
            foreach (var c in candidatesRaw)
            {
                string cand = Normalize(c);
                if (cand.Length == 0) continue;
                foreach (string token in set)
                    if (token.Length > 0 && (cand == token || cand.Contains(token) || token.Contains(cand)))
                        return true;
            }
            return false;
        }
    }
}
