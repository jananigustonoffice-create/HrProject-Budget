using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace HrProject_Budget.Data;

public class ParsedRow
{
    public string DepartmentHeader { get; set; } = "";
    public string Section { get; set; } = "";
    public int Group { get; set; }          // 0-based factory column group
    public int Budget { get; set; }
    public int Allocated { get; set; }
    public int Absent { get; set; }
}

public class ParsedSheet
{
    public string Name { get; set; } = "";
    public DateTime? Date { get; set; }
    public Dictionary<int, string> GroupHeaders { get; } = new(); // group -> factory heading text
    public List<ParsedRow> Rows { get; } = new();
}

/// <summary>
/// Reads the "Budgeted &amp; Actual MO/TMO Cadre" workbook layout: one sheet per date, with a Date cell (B3),
/// factory blocks of 5 columns (Budget, Allocated, Ab, Present, Shortage) starting at column B, and
/// department blocks introduced by a "(Department)" heading row.
/// </summary>
public static class ExcelReader
{
    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    const int FirstCol = 2, GroupWidth = 5, MaxGroups = 8;

    public static List<ParsedSheet> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        XDocument Load(string path)
        {
            var e = zip.GetEntry(path) ?? throw new InvalidDataException($"'{path}' not found - is this an .xlsx file?");
            using var s = e.Open();
            return XDocument.Load(s);
        }

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));

        var rels = Load("xl/_rels/workbook.xml.rels").Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        var result = new List<ParsedSheet>();
        foreach (var sh in Load("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var target = rels[(string)sh.Attribute(Rel + "id")!].TrimStart('/');
            var path = target.StartsWith("xl/") ? target : "xl/" + target;
            result.Add(ParseSheet((string)sh.Attribute("name")!, Load(path), shared));
        }
        return result;
    }

    static ParsedSheet ParseSheet(string name, XDocument doc, List<string> shared)
    {
        var grid = new SortedDictionary<int, Dictionary<int, string>>();
        foreach (var row in doc.Descendants(Main + "row"))
        {
            var rn = int.Parse((string)row.Attribute("r")!);
            var cells = new Dictionary<int, string>();
            foreach (var c in row.Elements(Main + "c"))
            {
                var col = ColIndex((string)c.Attribute("r")!);
                var t = (string?)c.Attribute("t");
                string? val;
                if (t == "inlineStr") val = string.Concat(c.Descendants(Main + "t").Select(x => x.Value));
                else
                {
                    val = (string?)c.Element(Main + "v");
                    if (val != null && t == "s") val = shared[int.Parse(val)];
                }
                if (!string.IsNullOrWhiteSpace(val)) cells[col] = val.Trim();
            }
            if (cells.Count > 0) grid[rn] = cells;
        }

        var sheet = new ParsedSheet { Name = name };
        if (grid.TryGetValue(3, out var r3) && r3.TryGetValue(2, out var d) &&
            double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 30000)
            sheet.Date = DateTime.FromOADate(serial).Date;

        string? dept = null;
        foreach (var (_, cells) in grid)
        {
            cells.TryGetValue(1, out var a);
            if (a == null) continue;

            if (a.Equals("ALLOCATED CADRE", StringComparison.OrdinalIgnoreCase))
            {
                if (sheet.GroupHeaders.Count == 0)
                    for (int g = 0; g < MaxGroups; g++)
                        if (cells.TryGetValue(FirstCol + g * GroupWidth, out var h) && !h.Equals("Group Total", StringComparison.OrdinalIgnoreCase))
                            sheet.GroupHeaders[g] = h;
                continue;
            }
            if (a.StartsWith('(') && cells.TryGetValue(FirstCol, out var b) && b.Equals("Budget", StringComparison.OrdinalIgnoreCase))
            {
                // "(Sewing Lines/CPD/Pre Pro)" is the summary table, not a department block.
                dept = a.Contains("/CPD", StringComparison.OrdinalIgnoreCase) ? null : a.Trim('(', ')').Trim();
                continue;
            }
            if (a.StartsWith("Sub Total", StringComparison.OrdinalIgnoreCase) || a.StartsWith("Grand Total", StringComparison.OrdinalIgnoreCase))
            { dept = null; continue; }
            if (dept == null) continue;

            for (int g = 0; g < MaxGroups; g++)
            {
                int col = FirstCol + g * GroupWidth;
                if (!sheet.GroupHeaders.ContainsKey(g)) continue;
                var budget = Num(cells, col);
                var alloc = Num(cells, col + 1);
                var ab = Num(cells, col + 2);
                if (budget == 0 && alloc == 0 && ab == 0) continue;
                sheet.Rows.Add(new ParsedRow { DepartmentHeader = dept, Section = a, Group = g, Budget = budget, Allocated = alloc, Absent = ab });
            }
        }
        return sheet;
    }

    static int Num(Dictionary<int, string> cells, int col) =>
        cells.TryGetValue(col, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (int)Math.Round(n) : 0;

    static int ColIndex(string cellRef)
    {
        int n = 0;
        foreach (var ch in cellRef) { if (!char.IsLetter(ch)) break; n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); }
        return n;
    }
}

