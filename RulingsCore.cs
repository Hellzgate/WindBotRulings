using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindBotRulings
{
    /// <summary>카드 1장의 재정 자료 (로컬 캐시 단위)</summary>
    #region 데이터·캐시 (RulingsData / RulingsStore)

    public sealed class RulingsData
    {
        public int Id { get; set; }
        public string NameKr { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string FactsText { get; set; } = "";        // 스크립트 기반(오프라인)
        public string OfficialText { get; set; } = "";     // 공식 JP Q&A (cid)
        public string OfficialUrl { get; set; } = "";
        public string YugipediaText { get; set; } = "";    // Yugipedia Card Rulings
        public string YugipediaUrl { get; set; } = "";
        public string Memo { get; set; } = "";             // 사용자 메모
        public string FetchedAt { get; set; } = "";
        public List<int> OfficialCids { get; set; } = new List<int>();
        public string NameJa { get; set; } = "";
    }

    /// <summary>%LOCALAPPDATA%\WindBotAIEditor\rulings\&lt;id&gt;.json 캐시</summary>
    public static class RulingsStore
    {
        public static string Dir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "WindBotAIEditor", "rulings");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string PathOf(int id) => Path.Combine(Dir, id + ".json");

        public static RulingsData Load(int id)
        {
            try
            {
                var p = PathOf(id);
                if (!File.Exists(p)) return new RulingsData { Id = id };
                var d = JsonSerializer.Deserialize<RulingsData>(File.ReadAllText(p, Encoding.UTF8));
                return d ?? new RulingsData { Id = id };
            }
            catch { return new RulingsData { Id = id }; }
        }

        public static void Save(RulingsData d)
        {
            try
            {
                File.WriteAllText(PathOf(d.Id),
                    JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>온라인 재정 수집기 — 공식(JP, cid) → Yugipedia 순</summary>
    #endregion

    #region 소스 수집 (이름·Yugipedia·공식 Q&A·텍스트 정리)

    public static class RulingsFetcher
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(25);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "WindBotRulings/1.0 (personal Yu-Gi-Oh! rulings viewer)");
            return c;
        }

        /// <summary>카드ID → 영문명 (YGOPRODeck API)</summary>
        public static async Task<string> ResolveEnglishNameAsync(int id)
        {
            try
            {
                var s = await Http.GetStringAsync("https://db.ygoprodeck.com/api/v7/cardinfo.php?id=" + id);
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    if (arr[0].TryGetProperty("name", out var n)) return n.GetString() ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>Yugipedia Card Rulings 위키텍스트</summary>
        public static async Task<(string text, string url, string raw, List<int> cids)> FetchYugipediaAsync(string nameEn)
        {
            if (string.IsNullOrWhiteSpace(nameEn)) return ("", "", "", new List<int>());
            foreach (var page in new[] { "Card Rulings:" + nameEn, "Card Rulings:" + nameEn + " (card)" })
            {
                try
                {
                    var u = "https://yugipedia.com/api.php?action=parse&prop=wikitext&format=json&formatversion=2&redirects=1&page="
                            + Uri.EscapeDataString(page);
                    var s = await Http.GetStringAsync(u);
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("error", out _)) continue;
                    if (!doc.RootElement.TryGetProperty("parse", out var parse)) continue;
                    if (!parse.TryGetProperty("wikitext", out var wt)) continue;
                    string raw = wt.GetString() ?? "";
                    string text = CleanWiki(raw);
                    string url = "https://yugipedia.com/wiki/" + Uri.EscapeDataString(page.Replace(' ', '_'));
                    if (text.Trim().Length == 0) continue;
                    return (text, url, raw, ExtractCids(raw));   // cid 는 **원문**에서 추출(ref 안에 있음)
                }
                catch { }
            }
            return ("", "", "", new List<int>());
        }

        /// <summary>위키텍스트에서 cid(공식 Q&A 번호) 목록 추출</summary>
        public static List<int> ExtractCids(string wikitext)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(wikitext)) return list;
            foreach (Match m in Regex.Matches(wikitext, @"[?&]cid=(\d+)"))
                if (int.TryParse(m.Groups[1].Value, out var v) && !list.Contains(v)) list.Add(v);
            return list;
        }

        /// <summary>공식 JP Q&A 페이지 (cid 직접 조회) — 태그 제거한 본문</summary>
        public static async Task<(string text, string url)> FetchOfficialAsync(int cid)
        {
            var url = "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=4&cid=" + cid + "&request_locale=ja";
            try
            {
                var html = await Http.GetStringAsync(url);
                string text = StripHtml(html);
                // 공식 페이지는 목록을 JS 로 그리는 경우가 많아, 본문이 아니라 "페이지 틀"만 긁히면 버린다
                if (LooksLikeChrome(text)) return ("", url);
                return (text, url);
            }
            catch { return ("", url); }
        }
        /// <summary>목록 HTML 에서 (fid, 날짜, 질문) 추출 — ope=4(cid별) / ope=2(키워드) 공통</summary>
        public static List<(int fid, string date, string question)> ParseRows(string html)
        {
            var list = new List<(int, string, string)>();
            if (string.IsNullOrEmpty(html)) return list;
            foreach (Match q in Regex.Matches(html, "<span class=\"name\">(.*?)</span>", RegexOptions.Singleline))
            {
                int start = q.Index;
                int next = html.IndexOf("<div class=\"t_row\">", start + q.Length, StringComparison.Ordinal);
                int end = next > 0 ? next : Math.Min(html.Length, start + 4000);
                string chunk = html.Substring(start, end - start);
                var f = Regex.Match(chunk, "fid=(\\d+)");
                if (!f.Success) continue;
                var d = Regex.Match(chunk, "更新日:</span>\\s*([0-9\\-]+)");
                string question = System.Net.WebUtility.HtmlDecode(Regex.Replace(q.Groups[1].Value, "<[^>]+>", "")).Trim();
                int fid = int.Parse(f.Groups[1].Value);
                if (list.Any(x => x.Item1 == fid)) continue;
                list.Add((fid, d.Success ? d.Groups[1].Value : "", question));
            }
            return list;
        }

        /// <summary>공식 Q&amp;A 전문 — cid &gt; 0 이면 카드별(ope=4), 아니면 키워드 검색(ope=2)</summary>
        public static async Task<(string text, string url, int count)> FetchOfficialAllAsync(int cid, string keyword, int maxItems = 30)
        {
            string listUrl = cid > 0
                ? "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=4&cid=" + cid + "&request_locale=ja"
                : "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=2&keyword=" + Uri.EscapeDataString(keyword ?? "") + "&request_locale=ja";
            var rows = new List<(int fid, string date, string question)>();
            try
            {
                rows = ParseRows(await Http.GetStringAsync(listUrl));
            }
            catch { }
            if (rows.Count == 0) return ("", listUrl, 0);
            if (rows.Count > maxItems) rows = rows.GetRange(0, maxItems);

            var sb = new StringBuilder();
            sb.AppendLine("[공식 Q&A] " + rows.Count + "건 (원문 · 일본어)" + (cid > 0 ? "  · cid " + cid : "  · 검색어 " + keyword));
            sb.AppendLine();
            int ok = 0;
            foreach (var row in rows)
            {
                var (q, a) = await FetchOfficialDetailAsync(row.fid);
                sb.AppendLine("── Q" + (ok + 1) + (row.date.Length > 0 ? "  (" + row.date + ")" : ""));
                sb.AppendLine("Q. " + (q.Length > 0 ? q : row.question));
                sb.AppendLine("A. " + a);
                sb.AppendLine("   https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=5&fid=" + row.fid + "&keyword=&tag=-1");
                sb.AppendLine();
                if (a.Length > 0) ok++;
                await Task.Delay(120);
            }
            return (sb.ToString(), listUrl, ok);
        }

        /// <summary>영문명 → 일본어 카드명 (Yugipedia ja_name · {{Ruby|표시|읽기}} 는 표시만)</summary>
        public static async Task<string> ResolveJapaneseNameAsync(string englishName)
        {
            if (string.IsNullOrWhiteSpace(englishName)) return "";
            try
            {
                var u = "https://yugipedia.com/api.php?action=parse&prop=wikitext&format=json&formatversion=2&redirects=1&page="
                        + Uri.EscapeDataString(englishName);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(u));
                if (!doc.RootElement.TryGetProperty("parse", out var parse)) return "";
                if (!parse.TryGetProperty("wikitext", out var wt)) return "";
                string text = wt.GetString() ?? "";
                var m = Regex.Match(text, @"(?m)^\s*\|\s*ja_name\s*=\s*(.+)$");
                if (!m.Success) return "";
                string v = m.Groups[1].Value.Trim();
                v = Regex.Replace(v, @"\{\{Ruby\|([^|}]+)\|[^|}]*\}\}", "$1");
                v = Regex.Replace(v, @"<[^>]+>", "");
                v = Regex.Replace(v, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");
                return v.Trim();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 공식 Q&A **목록** 수집 (ope=4) — 질문/날짜/fid 는 서버 렌더링이라 파싱 가능.
        /// 답변 본문은 상세(ope=5)에서 JS 로 그려져 자동 수집 불가 → 링크 제공.
        /// </summary>
        public static async Task<(string text, string url, int count)> FetchOfficialListAsync(int cid)
        {
            string url = "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=4&cid=" + cid + "&request_locale=ja";
            try
            {
                string html = await Http.GetStringAsync(url);
                // 질문(name) 위치부터 다음 행 전까지 잘라 fid/날짜를 찾는다 (fid 는 행 뒤 input.link_value 에 있음)
                var qs = Regex.Matches(html, "<span class=\"name\">(.*?)</span>", RegexOptions.Singleline);
                var rows = new List<string>();
                foreach (Match q in qs)
                {
                    int start = q.Index;
                    int next = html.IndexOf("<div class=\"t_row\">", start + q.Length, StringComparison.Ordinal);
                    int end = next > 0 ? next : Math.Min(html.Length, start + 4000);
                    string chunk = html.Substring(start, end - start);
                    string question = System.Net.WebUtility.HtmlDecode(Regex.Replace(q.Groups[1].Value, "<[^>]+>", "")).Trim();
                    var dfid = Regex.Match(chunk, "fid=(\\d+)");
                    var dd = Regex.Match(chunk, "更新日:</span>\\s*([0-9\\-]+)");
                    string date = dd.Success ? dd.Groups[1].Value : "";
                    string link = dfid.Success
                        ? "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=5&fid=" + dfid.Groups[1].Value + "&keyword=&tag=-1"
                        : url;
                    rows.Add("· " + question + (date.Length > 0 ? "   (" + date + ")" : "") + Environment.NewLine + "    " + link);
                }
                if (rows.Count == 0) return ("", url, 0);

                var sb = new StringBuilder();
                sb.AppendLine("[공식 Q&A 목록] 질문 " + rows.Count + "건 — 답변 본문은 사이트에서 확인하세요");
                sb.AppendLine("(답변은 상세 페이지에서 JS 로 표시되므로 자동 저장하지 않습니다)");
                sb.AppendLine();
                foreach (var r in rows) sb.AppendLine(r);
                return (sb.ToString(), url, rows.Count);
            }
            catch { }
            return ("", url, 0);
        }
        /// <summary>div#id 안의 텍스트 추출 (br → 개행, 태그 제거)</summary>
        public static string ExtractDivText(string html, string id)
        {
            var m = Regex.Match(html, "<div id=\"" + Regex.Escape(id) + "\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
            if (!m.Success) return "";
            string s = Regex.Replace(m.Groups[1].Value, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, "[ \t]+", " ");
            s = Regex.Replace(s, "\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>개별 Q&A(ope=5) 본문 — `div#question_text` / `div#answer_text` 에 **서버 렌더링**되어 있다</summary>
        public static async Task<(string question, string answer)> FetchOfficialDetailAsync(int fid)
        {
            string url = "https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=5&fid=" + fid + "&keyword=&tag=-1&request_locale=ja";
            try
            {
                string html = await Http.GetStringAsync(url);
                return (ExtractDivText(html, "question_text"), ExtractDivText(html, "answer_text"));
            }
            catch { }
            return ("", "");
        }




        /// <summary>본문이 아니라 사이트 틀(네비/로그인 등)만 긁힌 경우 판별</summary>
        public static bool LooksLikeChrome(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            int nav = 0;
            foreach (var k in new[] { "ログイン", "カード検索", "デッキ", "マイページ", "日本語", "English(Asia)", "簡体字", "遊戯王ニューロン" })
                if (text.Contains(k)) nav++;
            if (nav >= 3) return true;
            // 빈 줄/공백 비율이 지나치게 높으면 틀로 판단
            int ws = text.Count(char.IsWhiteSpace);
            if (text.Length > 0 && (double)ws / text.Length > 0.55) return true;
            return false;
        }

        // ---------- 텍스트 정리 ----------

        public static string CleanWiki(string wiki)
        {
            if (string.IsNullOrEmpty(wiki)) return "";
            string s = wiki;
            s = Regex.Replace(s, @"<ref[^>]*>.*?</ref>", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"<ref[^>]*/>", "");
            s = Regex.Replace(s, @"\{\{[^{}]*\}\}", "");                       // 템플릿(1단계)
            s = Regex.Replace(s, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");      // [[링크|표시]]
            s = Regex.Replace(s, @"'{2,}", "");                                 // 강조
            s = Regex.Replace(s, @"^=+\s*(.*?)\s*=+\s*$", "■ $1", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\*\s*", "· ", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = html;
            s = Regex.Replace(s, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</(p|div|li|tr|h\d)>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }
    }

    /// <summary>카드 Lua 에서 "기계적 사실"을 뽑는다 (공식 재정 아님 · 시뮬레이터 기준)</summary>
    #endregion

    #region 스크립트 사실 (Lua 파싱)

    public static class ScriptFacts
    {
        public static string? FindLua(string edoproRoot, int id)
        {
            if (string.IsNullOrWhiteSpace(edoproRoot) || id <= 0) return null;
            var file = "c" + id + ".lua";
            var direct = new[]
            {
                Path.Combine(edoproRoot, "script", "official", file),
                Path.Combine(edoproRoot, "script", "local", file),
                Path.Combine(edoproRoot, "script", file),
                Path.Combine(edoproRoot, "script", "pre-release", file),
            };
            foreach (var p in direct) if (File.Exists(p)) return p;

            try
            {
                var repos = Path.Combine(edoproRoot, "repositories");
                if (Directory.Exists(repos))
                    foreach (var p in Directory.GetFiles(repos, file, SearchOption.AllDirectories))
                        return p;
            }
            catch { }
            return null;
        }

        private static readonly (string token, string label)[] Tokens =
        {
            ("EFFECT_TYPE_ACTIVATE",        "발동형 (마법/함정)"),
            ("EFFECT_TYPE_IGNITION",        "기동 효과 (메인 페이즈 직접 발동)"),
            ("EFFECT_TYPE_TRIGGER_O",       "유발 효과 (임의)"),
            ("EFFECT_TYPE_TRIGGER_F",       "유발 효과 (필수)"),
            ("EFFECT_TYPE_QUICK_O",         "유발 즉시 효과 (임의, 체인 가능)"),
            ("EFFECT_TYPE_QUICK_F",         "유발 즉시 효과 (필수)"),
            ("EFFECT_TYPE_CONTINUOUS",      "지속 효과 (발동하지 않음)"),
            ("EFFECT_TYPE_SINGLE",          "단일 효과 (그 카드 자신)"),
            ("EFFECT_TYPE_FIELD",           "필드 효과"),
            ("EFFECT_TYPE_EQUIP",           "장착 효과"),
            ("EFFECT_SUMMON_PROC",          "소환 절차 (일반소환)"),
            ("EFFECT_SPSUMMON_PROC",        "특수소환 절차"),
            ("EFFECT_SPSUMMON_CONDITION",   "특수소환 조건"),
            ("EFFECT_SET_PROC",             "세트 절차"),
            ("EFFECT_FLAG_CARD_TARGET",     "대상 지정 효과"),
            ("EFFECT_FLAG_DAMAGE_STEP",     "데미지 스텝 관련"),
            ("EFFECT_CANNOT",               "금지 계열"),
            ("EFFECT_NEGATE",               "무효 계열"),
            ("EFFECT_UPDATE_ATTACK",        "공격력 변경"),
            ("EFFECT_UPDATE_DEFENSE",       "수비력 변경"),
            ("EFFECT_DESTROY_SUBSTITUTE",   "파괴 대체"),
            ("EFFECT_INDESTRUCTABLE",       "파괴 내성"),
        };

        public static string Build(string edoproRoot, int id)
        {
            var sb = new StringBuilder();
            var path = FindLua(edoproRoot, id);
            if (path == null)
            {
                sb.AppendLine("스크립트 없음 — 이 카드의 .lua 를 찾지 못했습니다.");
                sb.AppendLine("(프리릴리즈/커스텀 카드는 repositories 안에 있을 수 있습니다)");
                return sb.ToString();
            }

            string lua;
            try { lua = File.ReadAllText(path); }
            catch (Exception ex) { return "스크립트 읽기 실패: " + ex.Message; }

            sb.AppendLine("※ 스크립트 기반 기계적 사실 (공식 재정이 아닙니다)");
            sb.AppendLine("파일: " + path);
            sb.AppendLine();

            sb.AppendLine("[효과 종류]");
            var found = new List<string>();
            foreach (var (token, label) in Tokens)
                if (lua.Contains(token) && !found.Contains(label)) found.Add(label);
            if (found.Count == 0) sb.AppendLine("  (EFFECT_TYPE 토큰 없음 — 특수한 스크립트)");
            foreach (var f in found) sb.AppendLine("  · " + f);

            sb.AppendLine();
            sb.AppendLine("[제약/비용 흔적]");
            var misc = new List<string>();
            if (lua.Contains("SetCountLimit")) misc.Add("SetCountLimit — 턴/듀얼 횟수 제한 있음");
            if (lua.Contains("EFFECT_COUNT_CODE")) misc.Add("EFFECT_COUNT_CODE — '1턴에 1번' 계열 카운트");
            if (lua.Contains("CheckLPCost") || lua.Contains("PayLPCost")) misc.Add("LP 코스트 사용");
            if (lua.Contains("Release") || lua.Contains("ReleaseGroup")) misc.Add("릴리스(제물) 사용");
            if (lua.Contains("DiscardHand") || lua.Contains("DiscardDeck")) misc.Add("패/덱 버리기(코스트 계열)");
            if (lua.Contains("SelectEffectYesNo") || lua.Contains("SelectYesNo")) misc.Add("예/아니오 프롬프트 있음");
            if (lua.Contains("SelectOption")) misc.Add("옵션 선택 프롬프트 있음");
            if (lua.Contains("SelectPosition")) misc.Add("표시형식 선택 프롬프트 있음");
            if (misc.Count == 0) sb.AppendLine("  (특이 흔적 없음)");
            foreach (var m in misc) sb.AppendLine("  · " + m);

            sb.AppendLine();
            sb.AppendLine("[효과번호 후보 (SetDescription)]");
            var descs = new List<string>();
            foreach (Match m in Regex.Matches(lua, @"SetDescription\s*\(([^)]*)\)"))
            {
                var arg = m.Groups[1].Value.Trim();
                if (!descs.Contains(arg)) descs.Add(arg);
            }
            if (descs.Count == 0) sb.AppendLine("  (SetDescription 없음 → desc = 0, 효과 구분 불가)");
            foreach (var d in descs) sb.AppendLine("  · " + d);

            return sb.ToString();
        }
        #endregion
    }
}





