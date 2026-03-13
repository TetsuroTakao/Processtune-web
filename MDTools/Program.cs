using System.Text;

var root = args.Length >= 1 ? args[0] : @"E:\repos\ProcesstuneBlog\Data\output";
var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"Not found: {root}");
    return 1;
}

var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).ToList();
Console.WriteLine($"Target root: {root}");
Console.WriteLine($"Target files: {files.Count}");
Console.WriteLine($"Dry-run: {dryRun}");

int updated = 0, skipped = 0, noHit = 0;

foreach (var file in files)
{
    var original = await File.ReadAllTextAsync(file, Encoding.UTF8);
    var hasCrLf = original.Contains("\r\n");

    // 改行統一
    var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

    // "## Refer" の行番号を収集（前後空白は無視）
    var referIdx = new List<int>();
    for (int i = 0; i < lines.Count; i++)
    {
        if (lines[i].Trim().Equals("## Refer", StringComparison.OrdinalIgnoreCase))
            referIdx.Add(i);
    }

    if (referIdx.Count < 2)
    {
        noHit++;
        continue;
    }

    var last = referIdx[^1];

    // 最後の "## Refer" 行だけ削除（以降の内容は残す）
    lines.RemoveAt(last);

    // 書き戻し
    var rewritten = string.Join("\n", lines);
    if (hasCrLf) rewritten = rewritten.Replace("\n", "\r\n");

    // 元が末尾改行ありなら維持
    if (original.EndsWith("\r\n") && !rewritten.EndsWith("\r\n")) rewritten += "\r\n";
    if (original.EndsWith("\n") && !original.EndsWith("\r\n") && !rewritten.EndsWith("\n")) rewritten += "\n";

    Console.WriteLine($"{(dryRun ? "[DRY]" : "[UPDATE]")} {file}  (Refer count={referIdx.Count}, removed line={last + 1})");

    if (!dryRun)
        await File.WriteAllTextAsync(file, rewritten, Encoding.UTF8);

    updated++;
}

Console.WriteLine($"Done. Updated={updated}, Skipped={skipped}, NoTarget={noHit}");
return 0;