using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var inputDir = args.Length >= 1 ? args[0] : @"E:\repos\ProcesstuneBlog\Data\output";
var outputPath = args.Length >= 2 ? args[1] : Path.Combine(Environment.CurrentDirectory, "CategoryTagMap.json");

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Input directory not found: {inputDir}");
    return 1;
}

const string heading = "## カテゴリー";
const string tagPrefix = "https://blog.processtune.com/categories/tags/";

var mdFiles = Directory.EnumerateFiles(inputDir, "*.md", SearchOption.AllDirectories).ToList();
Console.WriteLine($"Markdown files: {mdFiles.Count}");

var linkRegex = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
var allTitles = new HashSet<string>(StringComparer.Ordinal);
var titleToSlug = new Dictionary<string, string>(StringComparer.Ordinal);

int processed = 0;
foreach (var file in mdFiles)
{
    string text;
    try
    {
        text = File.ReadAllText(file, Encoding.UTF8);
    }
    catch
    {
        // UTF-8で読めない場合の保険（BOM/既定）
        text = File.ReadAllText(file);
    }

    var section = ExtractFromHeadingToEnd(text, heading);
    if (section is null) continue;

    // (4) Markdownリンクの [text] を抽出 & (6) tagsリンクを抽出
    foreach (Match m in linkRegex.Matches(section))
    {
        var title = m.Groups[1].Value.Trim();
        var href  = m.Groups[2].Value.Trim();

        if (!string.IsNullOrWhiteSpace(title))
            allTitles.Add(title);

        if (href.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var slug = href.Substring(tagPrefix.Length).Trim('/');
            if (!string.IsNullOrWhiteSpace(title))
            {
                // 既に別slugが入ってたら最初優先（必要なら上書きに変更可）
                if (!titleToSlug.ContainsKey(title))
                    titleToSlug[title] = slug;
            }
        }
    }

    // (3) カンマ区切り文字列（未分類以外）を抽出
    // まずリンク表記を消して、残りからカンマ分割
    var noLinks = linkRegex.Replace(section, " ");
    foreach (var token in SplitCategories(noLinks))
    {
        if (token == "未分類") continue;
        allTitles.Add(token);
    }

    processed++;
}

Console.WriteLine($"Files with category section: {processed}");
Console.WriteLine($"Distinct titles (3+4): {allTitles.Count}");
Console.WriteLine($"Titles with tags link: {titleToSlug.Count}");

// (7) JSON出力： [{ "カテゴリ": "slug or 空" }, ...] 形式（要求どおり）
var output = new List<Dictionary<string, string>>();
foreach (var title in allTitles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    titleToSlug.TryGetValue(title, out var slug);
    slug ??= "";

    if (ShouldExclude(title, slug))
        continue;

    output.Add(new Dictionary<string, string> { [title] = slug ?? "" });
}

var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = true
});

File.WriteAllText(outputPath, json, Encoding.UTF8);
Console.WriteLine($"Output: {outputPath}");
return 0;


// ----------------- helpers -----------------

static string? ExtractFromHeadingToEnd(string markdown, string heading)
{
    // "## カテゴリー" が現れる位置から末尾まで
    var idx = markdown.IndexOf(heading, StringComparison.Ordinal);
    if (idx < 0) return null;
    return markdown.Substring(idx);
}

static IEnumerable<string> SplitCategories(string text)
{
    // "## カテゴリー" 以降のテキストから、カンマ区切りっぽい部分を抽出
    // 余計な行（見出し行/空行/「Categories:」など）を落とした上で分割
    // カンマは ',' '，' を想定
    var lines = text.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Where(l => !l.StartsWith("##", StringComparison.Ordinal)) // 見出し
                    .Select(l =>
                    {
                        // 先頭ラベルがあれば落とす（例: "Categories: ..."）
                        var p = l.IndexOf(':');
                        if (p > 0 && p < 20) return l[(p + 1)..].Trim();
                        return l;
                    });

    foreach (var line in lines)
    {
        // 1行に複数入ってる想定でカンマ分割
        foreach (var raw in line.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (string.IsNullOrWhiteSpace(t)) continue;

            // 末尾の句読点/余計な記号を軽く掃除
            t = t.Trim('・', '•', '-', '–', '—');

            if (!string.IsNullOrWhiteSpace(t))
                yield return t;
        }
    }
}

static bool ShouldExclude(string title, string slug)
{
    title = title?.Trim() ?? "";
    slug  = slug?.Trim() ?? "";

    // 追加(1) www.pyrla.com を含む
    if (title.Contains("www.pyrla.com", StringComparison.OrdinalIgnoreCase))
        return true;

    // 追加(2) "Previous" / "Next" で始まる
    if (title.StartsWith("Previous", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("Next", StringComparison.OrdinalIgnoreCase))
        return true;

    // 追加(3) "I use" / "I write" など “I + 動詞” で始まる（私は〜文）
    // 例: "I use ..." "I write ..." "I'm ..." などはノイズとして除外
    if (LooksLikeFirstPersonSentence(title))
        return true;

    // 既存(1) "Responses" を含み、slugが空
    if (string.IsNullOrWhiteSpace(slug) &&
        title.Contains("Responses", StringComparison.OrdinalIgnoreCase))
        return true;

    // 既存(2) 日付形式っぽいもの（slugの有無に関係なく除外）
    if (LooksLikeDate(title))
        return true;

    // 既存(3) 50文字以上 かつ slugが空
    if (string.IsNullOrWhiteSpace(slug) && title.Length >= 50)
        return true;

    return false;
}

static bool LooksLikeFirstPersonSentence(string s)
{
    // "I use", "I write" などのパターンをざっくり除外
    // 先頭の "I" や "I'm/I am/I’ve/I'd" なども対象にする
    // ただしカテゴリ名としてあり得る "iOS" などを誤除外しないため、先頭トークン限定にする
    // 例: "I use", "I write", "I am", "I'm", "I've", "I'd", "I was", "I will", "I can"
    return Regex.IsMatch(
        s,
        @"^\s*i\s+(use|write|am|was|were|will|can|have|had|do|did|think|like|love|need|want|prefer|made|make|built|build)\b",
        RegexOptions.IgnoreCase
    )
    || Regex.IsMatch(s, @"^\s*i\s*['’]m\b", RegexOptions.IgnoreCase)
    || Regex.IsMatch(s, @"^\s*i\s*['’](ve|d|ll)\b", RegexOptions.IgnoreCase);
}

static bool LooksLikeDate(string s)
{
    s = s.Trim();

    // 例: "April 15, 2014 at 12:14 pm"
    // 例: "April 15, 2014"
    // 例: "Apr 15, 2014"
    // 例: "... at 12:14 pm"
    var month = @"(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|jun(e)?|jul(y)?|aug(ust)?|sep(t(ember)?)?|oct(ober)?|nov(ember)?|dec(ember)?)";
    var datePattern = $@"^\s*{month}\s+\d{{1,2}},\s+\d{{4}}(\s+at\s+\d{{1,2}}:\d{{2}}\s*(am|pm))?\s*$";
    var timePattern = @"\bat\s+\d{1,2}:\d{2}\s*(am|pm)\b";

    if (Regex.IsMatch(s, datePattern, RegexOptions.IgnoreCase))
        return true;

    if (Regex.IsMatch(s, timePattern, RegexOptions.IgnoreCase))
        return true;

    return false;
}