using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

class Program
{
    private const string CategoryHrefPrefix = "https://blog.processtune.com/category/";

    static readonly Regex UploadPngRegex = new(@"^/wp-content/uploads/(?<yyyy>\d{4})/(?<mm>\d{2})/(?<rest>.+?\.png)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex DateTextRegex = new(@"^(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // azurewebsites の uploads を images/YYYYMM/ に変換（拡張子は主要画像を許可）
    static readonly Regex AzureWebsitesUploadRegex = new(@"^/wp-content/uploads/(?<yyyy>\d{4})/(?<mm>\d{2})/(?<file>[^/?#]+\.(png|jpg|jpeg|gif|webp))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // www.processtune.com/blog/?p=xxxx を categories/posts に変換
    static readonly Regex ProcesstuneBlogPostIdRegex = new(@"^/blog/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ★MD化すると邪魔なナビ/パンくず等はリンク化しない（出力もしない）
    private static readonly HashSet<string> SkipLinkTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skip to content",
        "Processtune Blog",
        "Microsoft Azure",
        "Azure Active Directory",
        "Visual Studio",
        "Microservice",
        "リンク",
        "SlideShow",
        "Cancel reply",
        "Colibri",
        "Silverlight"
    };

    // ★リンクのベース（指定どおり固定）
    private const string BaseFileUri = "file:///E:/repos/Processtune-web/www/";

    private static readonly Regex TitleRegex = new(
        @"<title\b[^>]*>(?<t>[\s\S]*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*\bhref\s*=\s*([""'])(?<href>.*?)\1[^>]*>(?<text>[\s\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NewlineTokenDupRegex = new(@"(%改行%)(\s*%改行%)+", RegexOptions.Compiled);

    private static readonly Regex NewlineDupRegex = new(@"(\n\s*){2,}", RegexOptions.Compiled);

    private static readonly HashSet<string> BreakTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br","p","div","li","ul","ol","pre","blockquote",
        "h1","h2","h3","h4","h5","h6",
        "table","tr","td","th","thead","tbody","tfoot",
        "section","article","header","footer","main","nav","aside","hr"
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script","style","noscript","template"
    };

    static int Main(string[] args)
    {
        // root = targetpath.txt が置いてあるフォルダ
        // var root = args.Length >= 1 ? args[0] : Directory.GetCurrentDirectory();
        var root = args.Length >= 1 ? args[0] : @"E:\repos\Processtune-web\www";
        root = @"E:\repos\Processtune-web\www\wp-content\uploads";
        root = Path.GetFullPath(root);

        if (IsUploadsRoot(root))
        {
            CopyUploadsPngToOutputImages(root);
            return 0;
        }

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Directory not found: {root}");
            return 1;
        }

        var listFile = Path.Combine(root, "targetpath.txt");
        if (!File.Exists(listFile))
        {
            Console.Error.WriteLine($"targetpath.txt not found: {listFile}");
            return 1;
        }

        // output フォルダ
        var outDir = Path.Combine(root, "output");
        Directory.CreateDirectory(outDir);

        // CSV 出力先
        var outCsv = Path.Combine(outDir, "titles.csv");

        // 対象拡張子
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".htm", ".html" };

        // targetpath.txt の各行 = 対象ファイル
        // - 空行、#コメント行無視
        // - 相対パスは root からの相対として扱う
        var files = File.ReadAllLines(listFile)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !l.StartsWith("#"))
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(root, p))
            .Select(Path.GetFullPath)
            .Where(p => exts.Contains(Path.GetExtension(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var fs = new FileStream(outCsv, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // CSV ヘッダ（戻したプログラム同等）
        sw.WriteLine("Title,Categories,BodyText,Uri,FullPath");

        int count = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"Skip (not found): {file}");
                continue;
            }

            string html;
            try
            {
                html = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Read failed: {file} ({ex.Message})");
                continue;
            }

            var title = ExtractTitle(html);
            var categories = ExtractCategories(html, CategoryHrefPrefix);
            var bodyText = ExtractBodyText(html);                 // CSV用（%改行%、図（）、リンク（））
            var uri = new Uri(file).AbsoluteUri;

            // CSV 1行
            sw.WriteLine($"{Csv(title)},{Csv(categories)},{Csv(bodyText)},{Csv(uri)},{Csv(Path.GetFullPath(file))}");

            // ★ MD 出力（title.md）
            var mdFileName = MakeSafeFileName(title);
            if (string.IsNullOrWhiteSpace(mdFileName))
                mdFileName = $"untitled_{count + 1}";
            mdFileName += ".md";

            var mdPath = Path.Combine(outDir, mdFileName);

            // md本文：body（\n改行、Markdownリンク） + カテゴリ
            // var mdBody = ExtractBodyMarkdown(html, file);
            var mdBody = ExtractBodyMarkdown(html, file, out var movedCategoriesBlock);
            var md = new StringBuilder(16_384);
            md.Append(mdBody);

            if (md.Length > 0 && md[^1] != '\n') md.Append('\n');

            md.Append("## カテゴリー\n");
            md.Append(categories); // CSVと同じ「cat1,cat2,...」

            if (!string.IsNullOrWhiteSpace(movedCategoriesBlock))
            {
                if (md.Length > 0 && md[^1] != '\n') md.Append('\n');
                md.Append(movedCategoriesBlock.Trim()).Append('\n');
            }

            // 文字コードUTF-8（BOM無し）
            File.WriteAllText(mdPath, md.ToString(), new UTF8Encoding(false));

            count++;
        }

        Console.WriteLine($"Done. {count} files -> {outCsv} and {outDir}\\*.md");
        return 0;
    }

    static string ExtractTitle(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var m = TitleRegex.Match(html);
        if (!m.Success) return "";
        var t = m.Groups["t"].Value;
        t = WsRegex.Replace(t, " ").Trim();
        return System.Net.WebUtility.HtmlDecode(t);
    }

    static string ExtractCategories(string html, string hrefPrefix)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in AnchorRegex.Matches(html))
        {
            var href = m.Groups["href"].Value.Trim();
            if (!href.StartsWith(hrefPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var inner = m.Groups["text"].Value;
            inner = TagRegex.Replace(inner, "");
            inner = System.Net.WebUtility.HtmlDecode(inner);
            inner = WsRegex.Replace(inner, " ").Trim();

            if (!string.IsNullOrWhiteSpace(inner))
                set.Add(inner);
        }

        return string.Join(",", set.OrderBy(x => x));
    }

    // CSV用（元の仕様）：img→図（url）, a→リンク（href）, 改行タグ→%改行%
    static string ExtractBodyText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var doc = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        doc.LoadHtml(html);

        var body = doc.DocumentNode.SelectSingleNode("//body");
        if (body is null) return "";

        var sb = new StringBuilder(16_384);
        Walk(body, sb);

        var s = sb.ToString();
        s = s.Replace("\r", "").Replace("\n", "");
        s = WsRegex.Replace(s, " ").Trim();
        s = NewlineTokenDupRegex.Replace(s, "$1");
        s = s.Replace(" %改行%", "%改行%").Replace("%改行% ", "%改行%").Trim();
        return s;

        static void Walk(HtmlNode node, StringBuilder sb)
        {
            if (node.NodeType == HtmlNodeType.Element)
            {
                var name = node.Name;

                if (SkipTags.Contains(name))
                    return;

                if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    var src = (node.GetAttributeValue("src", "") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(src))
                        src = (node.GetAttributeValue("data-src", "") ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(src))
                        sb.Append($"図（{src}）");
                    return;
                }

                if (name.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    var href = (node.GetAttributeValue("href", "") ?? "").Trim();
                    sb.Append($"リンク（{href}）");
                    return;
                }

                if (BreakTags.Contains(name))
                    sb.Append("%改行%");

                foreach (var child in node.ChildNodes)
                    Walk(child, sb);
            }
            else if (node.NodeType == HtmlNodeType.Text)
            {
                var text = node.InnerText;
                if (string.IsNullOrWhiteSpace(text))
                    return;

                text = System.Net.WebUtility.HtmlDecode(text);
                text = WsRegex.Replace(text, " ").Trim();
                if (text.Length > 0)
                    sb.Append(text);
            }
        }
    }

    // ★ Markdown用：改行は \n、リンクは [text](uri)、uriは BaseFileUri から ./ 相対
    static string ExtractBodyMarkdown(string html, string currentFilePath, out string movedCategoriesBlock)
    {
        movedCategoriesBlock = "";
        if (string.IsNullOrEmpty(html)) return "";

        var doc = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        doc.LoadHtml(html);

        var body = doc.DocumentNode.SelectSingleNode("//body");
        if (body is null) return "";

        var sb = new StringBuilder(16_384);
        WalkMd(body, sb);

        var s = sb.ToString();

        // 改行整理：連続改行は1つに圧縮（必要なければ削除OK）
        s = NewlineDupRegex.Replace(s, "\n");
        // 行頭/行末の空白整理
        s = s.Split('\n').Select(line => line.Trim()).Aggregate(new StringBuilder(), (acc, line) =>
        {
            if (line.Length == 0)
            {
                acc.Append('\n');
                return acc;
            }
            acc.Append(line).Append('\n');
            return acc;
        }).ToString().TrimEnd('\n');
        // ★Tags:\nNo Tag を削除、Categories: 中身を取り出して末尾へ移動
        (s, movedCategoriesBlock) = ExtractAndRemoveCategoriesTagsBlock(s);
        s = ConvertDotBulletLinesToH2(s);
        s = EnsureFirstLineAsH1(s);  // 既に入れてるならそのまま
        return s;        
        // return EnsureFirstLineAsH1(s);

        static void WalkMd(HtmlNode node, StringBuilder sb)
        {
            if (node.NodeType == HtmlNodeType.Element)
            {
                var name = node.Name;

                if (SkipTags.Contains(name))
                    return;

                if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    var src = (node.GetAttributeValue("src", "") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(src))
                        src = (node.GetAttributeValue("data-src", "") ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(src))
                        src = RewriteProcesstuneHref(src);
                        // 図は Markdown リンクとして出す
                        sb.Append("[図](").Append(ToRelativeMarkdownUri(src)).Append(')');
                    return;
                }

                if (name.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    var hrefRaw = (node.GetAttributeValue("href", "") ?? "").Trim();
                    var textRaw = node.InnerText ?? "";
                    var text = System.Net.WebUtility.HtmlDecode(WsRegex.Replace(textRaw, " ").Trim());
                    if (hrefRaw.Contains("1drv.ms", StringComparison.OrdinalIgnoreCase))return;
                    if (text.Equals("未分類", StringComparison.OrdinalIgnoreCase) || hrefRaw.Contains("/categories/tags/%E6%9C%AA%E5%88%86%E9%A1%9E", StringComparison.OrdinalIgnoreCase) ||
                        System.Net.WebUtility.UrlDecode(hrefRaw).Contains("/categories/tags/未分類", StringComparison.OrdinalIgnoreCase)) return;
                    if(SkipLinkTexts.Contains(text)) return;
                    if (string.IsNullOrWhiteSpace(hrefRaw) || hrefRaw == "#" || hrefRaw.StartsWith("#")) return;
                    if (IsDateText(text))
                    {
                        ReplaceTrailingOnWithPostOn(sb);
                        sb.Append(text);
                        return;
                    }
                    var href = RewriteProcesstuneHref(hrefRaw);
                    var mdUri = ToRelativeMarkdownUri(href);
                    sb.Append('[').Append(EscapeMdText(text)).Append(']')
                      .Append('(').Append(mdUri).Append(')');
                    return;
                }

                if (BreakTags.Contains(name))
                    sb.Append('\n');

                foreach (var child in node.ChildNodes)
                    WalkMd(child, sb);
            }
            else if (node.NodeType == HtmlNodeType.Text)
            {
                var text = node.InnerText;
                if (string.IsNullOrWhiteSpace(text))
                    return;

                text = System.Net.WebUtility.HtmlDecode(text);
                text = WsRegex.Replace(text, " ").Trim();
                if (text.Length > 0)
                    sb.Append(text);
            }
        }

        static string EscapeMdText(string s)
        {
            // 最低限：[] をエスケープ（必要なら増やす）
            return s.Replace("[", "\\[").Replace("]", "\\]");
        }

        static string ToRelativeMarkdownUri(string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return "";

            // すでに ./ ならそのまま
            if (href.StartsWith("./")) return href;

            // file:///E:/repos/Processtune-web/www/... → ./...
            if (href.StartsWith(BaseFileUri, StringComparison.OrdinalIgnoreCase))
                return "./" + href.Substring(BaseFileUri.Length);

            // /2011/... → ./2011/...
            if (href.StartsWith("/"))
                return "./" + href.TrimStart('/');

            // http(s) 等はそのまま
            return href;
        }
    }

    static string MakeSafeFileName(string title)
    {
        title ??= "";
        title = title.Trim();
        if (title.Length == 0) return "";

        foreach (var c in Path.GetInvalidFileNameChars())
            title = title.Replace(c, '_');

        // 末尾のドットや空白は避ける
        title = title.Trim().TrimEnd('.');

        // 長すぎるのを軽く制限
        // if (title.Length > 120) title = title.Substring;

        return title;
    }

    static string Csv(string s)
    {
        s ??= "";
        s = s.Replace("\"", "\"\"");
        return $"\"{s}\"";
    }
    static string EnsureFirstLineAsH1(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        // 末尾改行を揃える
        markdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = markdown.Split('\n');

        // 先頭から最初の非空行を探す
        int idx = 0;
        while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx]))
            idx++;

        if (idx >= lines.Length) return markdown.Trim(); // 全部空行

        var titleLine = lines[idx].Trim();

        // 既に "# " で始まっているならそのまま（重複防止）
        if (titleLine.StartsWith("# "))
            return markdown.TrimEnd('\n');

        // 本文：titleLine を除外し、さらに先頭の空行も除去
        var bodyLines = lines
            .Skip(idx + 1)
            .ToList();

        while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[0]))
            bodyLines.RemoveAt(0);

        var body = string.Join("\n", bodyLines).TrimEnd('\n');

        return body.Length == 0
            ? $"# {titleLine}"
            : $"# {titleLine}\n{body}";
    }
    static string RewriteProcesstuneHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return href;

        // 1) author -> profiles
        if (href.StartsWith("https://blog.processtune.com/author/tetsuro-takao/", StringComparison.OrdinalIgnoreCase) ||
            href.Equals("https://blog.processtune.com/author/tetsuro-takao", StringComparison.OrdinalIgnoreCase))
        {
            return "https://blog.processtune.com/profiles/microsoftmvp";
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var u))
            return href;

        // 2) 1drv.ms はここでは何もしない（a側で「削除」判定する）
        //    ※ Rewrite では触らない

        // 3) blog.processtune.com の uploads/YYYY/MM/*.png -> images/YYYYMM/*.png
        if (u.Host.Equals("blog.processtune.com", StringComparison.OrdinalIgnoreCase))
        {
            var m = UploadPngRegex.Match(u.AbsolutePath);
            if (m.Success)
            {
                var yyyymm = m.Groups["yyyy"].Value + m.Groups["mm"].Value;
                var rest = m.Groups["rest"].Value; // ファイル名.png
                return $"https://blog.processtune.com/images/{yyyymm}/{rest}";
            }

            // 4) category -> categories/tags (path segments join by '-')
            if (u.AbsolutePath.StartsWith("/category/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = u.AbsolutePath.Substring("/category/".Length).Trim('/');
                if (tail.Length > 0)
                {
                    var slug = string.Join("-", tail.Split('/', StringSplitOptions.RemoveEmptyEntries));
                    return $"https://blog.processtune.com/categories/tags/{slug}";
                }
            }
        }

        // 5) azurewebsites の uploads/YYYY/MM/file -> blog.processtune.com/images/YYYYMM/file （https固定）
        //    hostに "azurewebsites" が入っていれば対象
        if (u.Host.Contains("azurewebsites", StringComparison.OrdinalIgnoreCase))
        {
            var m = AzureWebsitesUploadRegex.Match(u.AbsolutePath);
            if (m.Success)
            {
                var yyyymm = m.Groups["yyyy"].Value + m.Groups["mm"].Value;
                var file = m.Groups["file"].Value;
                return $"https://blog.processtune.com/images/{yyyymm}/{file}";
            }
        }

        // 6) www.processtune.com/blog/?p=xxxx -> http://www.processtune.com/categories/posts
        if (u.Host.Equals("www.processtune.com", StringComparison.OrdinalIgnoreCase) &&
            ProcesstuneBlogPostIdRegex.IsMatch(u.AbsolutePath) &&
            u.Query.Contains("p=", StringComparison.OrdinalIgnoreCase))
        {
            return "http://www.processtune.com/categories/posts";
        }
        // docs.microsoft.com / developer.microsoft.com -> learn.microsoft.com
        if (u.Host.Equals("docs.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            u.Host.Equals("developer.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            var b = new UriBuilder(u)
            {
                Scheme = "https",
                Host = "learn.microsoft.com",
                Port = -1
            };
            return b.Uri.ToString();
        }
        return href;
    }
    static bool IsDateText(string text) => DateTextRegex.IsMatch(text.Trim());
    static void ReplaceTrailingOnWithPostOn(StringBuilder sb)
    {
        // 末尾が "on " のときだけ置換
        if (sb.Length < 3) return;
        var tail = sb.ToString(Math.Max(0, sb.Length - 3), Math.Min(3, sb.Length));
        tail = tail.Replace("\n","");
        if (tail.Equals("on ", StringComparison.OrdinalIgnoreCase))
        {
            sb.Length -= 3;
            sb.Append(" Post on ");
        }
        if (tail.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            sb.Length -= 3;
            sb.Append(" Post on ");
        }
    }
    static (string body, string movedCats) ExtractAndRemoveCategoriesTagsBlock(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return (markdown, "");

        var s = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

        // "Tags:\nNo Tag" を丸ごと消す（どこにあっても）
        s = Regex.Replace(s, @"\bTags:\s*\n\s*No Tag\b\s*\n?", "", RegexOptions.IgnoreCase);

        // "Categories:" を見つけたら、その後ろ（次の Tags: まで）を抜いて末尾へ
        // （Categories: ラベル自体は出力しない）
        var idx = IndexOfIgnoreCase(s, "Categories:");
        if (idx < 0) return (s, "");

        // Categories: の直後位置
        var start = idx + "Categories:".Length;

        // 次の "Tags:" が残っている場合の終端（No Tag は既に消してるが保険）
        var idxTags = IndexOfIgnoreCase(s, "Tags:", start);

        int end = (idxTags >= 0) ? idxTags : s.Length;

        // Categories: 直後〜終端まで（リンク群）を抜く
        var cats = s.Substring(start, end - start).Trim();

        // 元の場所から Categories: ラベル＋中身を削除
        // Categories: が単独行でも同一行でも対応するため、前後の余計な改行も軽く整形
        var before = s.Substring(0, idx);
        var after = s.Substring(end);

        var body = (before + after).Trim();

        return (body, cats);
    }

    static int IndexOfIgnoreCase(string s, string value, int startIndex = 0)
        => s.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
    static string ConvertDotBulletLinesToH2(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        markdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = markdown.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // 行頭（先頭空白含む）で「・」
            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length > 0 && (trimmedStart[0] == '・' || trimmedStart[0] == '•'))
            {
                var title = trimmedStart.Substring(1).Trim(); // 「・」を除去
                if (!string.IsNullOrWhiteSpace(title))
                {
                    lines[i] = "## " + title;
                }
                else
                {
                    // 「・」だけの行は消す（必要なら維持でもOK）
                    lines[i] = "";
                }
            }
        }

        return string.Join("\n", lines);
    }
    static bool IsUploadsRoot(string root)
    {
        var p = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.EndsWith(Path.Combine("wp-content", "uploads"), StringComparison.OrdinalIgnoreCase);
    }
    static void CopyUploadsPngToOutputImages(string uploadsRoot)
    {
        // uploadsRoot = E:\repos\Processtune-web\www\wp-content\uploads
        var uploadsDir = new DirectoryInfo(uploadsRoot);
        var wpContentDir = uploadsDir.Parent;           // ...\www\wp-content
        var wwwDir = wpContentDir?.Parent;              // ...\www
        if (wwwDir == null)
            throw new DirectoryNotFoundException($"Cannot find www root from: {uploadsRoot}");

        var outputImagesRoot = Path.Combine(wwwDir.FullName, "output", "images");
        Directory.CreateDirectory(outputImagesRoot);

        int copied = 0, skipped = 0, badPath = 0;

        foreach (var src in Directory.EnumerateFiles(uploadsRoot, "*.png", SearchOption.AllDirectories))
        {
            // uploads\YYYY\MM\file.png を想定
            var rel = Path.GetRelativePath(uploadsRoot, src);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToArray();

            if (parts.Length < 3)
            {
                badPath++;
                continue;
            }

            var yyyy = parts[0];
            var mm = parts[1];
            var fileName = parts[^1];

            if (yyyy.Length != 4 || !yyyy.All(char.IsDigit) ||
                mm.Length != 2 || !mm.All(char.IsDigit))
            {
                badPath++;
                continue;
            }

            var yyyymm = yyyy + mm;
            var destDir = Path.Combine(outputImagesRoot, yyyymm);
            Directory.CreateDirectory(destDir);

            var dest = Path.Combine(destDir, fileName);

            // 上書き方針：既にあればスキップ（必要なら true にして上書きも可）
            if (File.Exists(dest))
            {
                skipped++;
                continue;
            }

            File.Copy(src, dest);
            copied++;
        }

        Console.WriteLine($"PNG copy done. copied={copied}, skipped={skipped}, badPath={badPath}");
    }
}