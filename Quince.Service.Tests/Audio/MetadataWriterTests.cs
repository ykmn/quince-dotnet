using System.Text;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class MetadataWriterTests : IDisposable
{
    private readonly string _tempDir;

    public MetadataWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("quince-meta-test-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static MetadataEvent Evt(string title, string artist = "", DateTimeOffset? ts = null) =>
        new(artist.Length > 0 ? $"{artist} - {title}" : title, artist, title, ts ?? DateTimeOffset.Now);

    private static string[] FindCsvFiles(string baseDir) =>
        Directory.Exists(baseDir) ? Directory.GetFiles(baseDir, "*.csv", SearchOption.AllDirectories) : Array.Empty<string>();

    private static List<string[]> ReadCsvRows(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        return lines.Skip(1).Where(l => l.Length > 0).Select(ParseCsvLine).ToList();
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != '"') { i++; continue; }
            i++;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i += 2; continue; }
                    i++;
                    break;
                }
                current.Append(line[i]);
                i++;
            }
            fields.Add(current.ToString());
            current.Clear();
            if (i < line.Length && line[i] == ',') i++;
        }
        return fields.ToArray();
    }

    [Fact]
    public void SingleEvent_NoCsvYet()
    {
        var mw = new MetadataWriter(_tempDir, "");
        mw.OnMetadata(Evt("Freedom", "George Michael", new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        Assert.Empty(FindCsvFiles(_tempDir));
    }

    [Fact]
    public void TwoEvents_CreatesCsvWithFirstRow()
    {
        var mw = new MetadataWriter(_tempDir, "");
        var t0 = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
        mw.OnMetadata(Evt("Freedom", "George Michael", t0));
        mw.OnMetadata(Evt("Frozen", "Madonna", t0.AddSeconds(200.5)));

        var csv = Assert.Single(FindCsvFiles(_tempDir));
        var rows = ReadCsvRows(csv);
        var row = Assert.Single(rows);
        Assert.Equal("Freedom", row[1]);
        Assert.Equal("George Michael", row[2]);
        Assert.Equal("M", row[3]);
        Assert.Equal("3:20.500", row[4]);
    }

    [Fact]
    public void Flush_WritesPendingRowWithoutLength()
    {
        var mw = new MetadataWriter(_tempDir, "");
        mw.OnMetadata(Evt("Freedom", "George Michael", new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var csv = Assert.Single(FindCsvFiles(_tempDir));
        var row = Assert.Single(ReadCsvRows(csv));
        Assert.Equal("Freedom", row[1]);
        Assert.Equal("", row[4]);
    }

    [Fact]
    public void OnSilence_FinalisesAndAddsGapRow()
    {
        var mw = new MetadataWriter(_tempDir, "");
        var ts = DateTimeOffset.Now.AddSeconds(-5);
        mw.OnMetadata(Evt("Freedom", "George Michael", ts));
        mw.OnSilence();

        var allRows = FindCsvFiles(_tempDir).SelectMany(ReadCsvRows).ToList();
        Assert.Equal(2, allRows.Count);
        Assert.Equal("Freedom", allRows[0][1]);
        Assert.NotEqual("", allRows[0][4]);
        Assert.Equal("", allRows[1][1]);
        Assert.Equal("", allRows[1][2]);
        Assert.Equal("", allRows[1][3]);
        Assert.Equal("", allRows[1][4]);
    }

    [Theory]
    [InlineData(200.5, "3:20.500")]
    [InlineData(3661.1, "1:01:01.100")]
    [InlineData(62.0, "1:02.000")]
    public void FormatDuration_MatchesLegacy(double seconds, string expected)
    {
        Assert.Equal(expected, MetadataWriter.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void CsvLivesInMetaSubfolder_WhenNoExplicitMetadataPath()
    {
        var mw = new MetadataWriter(_tempDir, "");
        mw.OnMetadata(Evt("Freedom", ts: new DateTimeOffset(2026, 6, 28, 16, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var expected = Path.Combine(_tempDir, "meta", "2026-06-28.csv");
        Assert.True(File.Exists(expected), $"Expected {expected}");
    }

    [Fact]
    public void CsvHasCorrectHeader()
    {
        var mw = new MetadataWriter(_tempDir, "");
        mw.OnMetadata(Evt("Freedom", ts: new DateTimeOffset(2026, 6, 28, 16, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var csv = Assert.Single(FindCsvFiles(_tempDir));
        var header = ParseCsvLine(File.ReadAllLines(csv, Encoding.UTF8)[0]);
        Assert.Equal(new[] { "EventTime", "ElemName", "ElemArtist", "ElemClass", "ElemLength" }, header);
    }

    [Fact]
    public void DayRollover_TwoCsvFiles()
    {
        var mw = new MetadataWriter(_tempDir, "");
        var day1 = new DateTimeOffset(2026, 6, 28, 23, 59, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 6, 29, 0, 1, 0, TimeSpan.Zero);

        mw.OnMetadata(Evt("Track A", ts: day1));
        mw.OnMetadata(Evt("Track B", ts: day2));
        mw.OnMetadata(Evt("Track C", ts: day2.AddSeconds(180)));
        mw.Flush();

        var csvDay1 = Path.Combine(_tempDir, "meta", "2026-06-28.csv");
        var csvDay2 = Path.Combine(_tempDir, "meta", "2026-06-29.csv");
        Assert.True(File.Exists(csvDay1));
        Assert.True(File.Exists(csvDay2));

        Assert.Equal("Track A", ReadCsvRows(csvDay1)[0][1]);
        Assert.Contains(ReadCsvRows(csvDay2), r => r[1] == "Track B");
    }

    [Fact]
    public void EmptyArtist_WrittenAsEmptyQuotedField_NotOmitted()
    {
        var mw = new MetadataWriter(_tempDir, "");
        var t0 = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
        mw.OnMetadata(Evt("Мадонна", ts: t0));
        mw.OnMetadata(Evt("Next Track", ts: t0.AddSeconds(100)));

        var csv = Assert.Single(FindCsvFiles(_tempDir));
        var text = File.ReadAllText(csv, Encoding.UTF8);
        var dataLine = text.Split('\n').First(l => l.Contains("Мадонна"));
        Assert.Equal(4, dataLine.Count(c => c == ','));
        var row = ParseCsvLine(dataLine);
        Assert.Equal("", row[2]);
    }

    [Fact]
    public void ExplicitMetadataPath_OverridesDefaultMetaSubfolder()
    {
        var explicitDir = Path.Combine(_tempDir, "custom-meta");
        var mw = new MetadataWriter(_tempDir, explicitDir);
        mw.OnMetadata(Evt("Freedom", ts: new DateTimeOffset(2026, 6, 28, 16, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        Assert.True(File.Exists(Path.Combine(explicitDir, "2026-06-28.csv")));
    }

    [Theory]
    [InlineData("Реклама")]
    [InlineData("реклама")]
    [InlineData("РЕКЛАМА")]
    [InlineData("Блок реклама на радио")]
    public void AdKeyword_MatchInTitle_ClassifiedAsC(string title)
    {
        var mw = new MetadataWriter(_tempDir, "", () => new[] { "Реклама", "Reklama", "Commercial" });
        mw.OnMetadata(Evt(title, ts: new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var row = Assert.Single(ReadCsvRows(Assert.Single(FindCsvFiles(_tempDir))));
        Assert.Equal("C", row[3]);
    }

    [Fact]
    public void AdKeyword_MatchInArtist_ClassifiedAsC()
    {
        var mw = new MetadataWriter(_tempDir, "", () => new[] { "Commercial" });
        mw.OnMetadata(Evt("Break", "Commercial Break Network", new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var row = Assert.Single(ReadCsvRows(Assert.Single(FindCsvFiles(_tempDir))));
        Assert.Equal("C", row[3]);
    }

    [Fact]
    public void NoAdKeywordMatch_ClassifiedAsM()
    {
        var mw = new MetadataWriter(_tempDir, "", () => new[] { "Реклама", "Reklama", "Commercial" });
        mw.OnMetadata(Evt("Freedom", "George Michael", new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var row = Assert.Single(ReadCsvRows(Assert.Single(FindCsvFiles(_tempDir))));
        Assert.Equal("M", row[3]);
    }

    [Fact]
    public void NoAdKeywordsConfigured_ClassifiedAsM()
    {
        var mw = new MetadataWriter(_tempDir, "");
        mw.OnMetadata(Evt("Реклама", ts: new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero)));
        mw.Flush();

        var row = Assert.Single(ReadCsvRows(Assert.Single(FindCsvFiles(_tempDir))));
        Assert.Equal("M", row[3]);
    }
}
