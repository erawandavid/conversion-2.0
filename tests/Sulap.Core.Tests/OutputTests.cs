using System.Security.AccessControl;
using System.Security.Principal;
using Sulap.Core.Formats;
using Sulap.Core.Output;

namespace Sulap.Core.Tests;

public sealed class OutputTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Names_keep_the_base_name_and_count_up_on_clashes()
    {
        Assert.Equal("Report.jpg", OutputNames.Candidate("Report", ".jpg", 1));
        Assert.Equal("Report (2).jpg", OutputNames.Candidate("Report", ".jpg", 2));
        Assert.Equal("Report (JPG)", OutputNames.PagesFolder("Report", FileFormat.Jpeg));
        Assert.Equal("Page 12.png", OutputNames.PageFile(12, FileFormat.Png));
    }

    [Theory]
    [InlineData("Data", "Data.csv")]
    [InlineData("Q1: Sales / Costs", "Q1_ Sales _ Costs.csv")]
    [InlineData("  Trailing dots...  ", "Trailing dots.csv")]
    [InlineData("CON", "Sheet.csv")]
    [InlineData("nul.backup", "Sheet.csv")]
    [InlineData("COM3", "Sheet.csv")]
    [InlineData("", "Sheet.csv")]
    [InlineData("...", "Sheet.csv")]
    public void Sheet_names_become_names_files_can_have(string sheetName, string expected)
    {
        Assert.Equal(expected, OutputNames.SheetFile(sheetName, FileFormat.Csv));
    }

    [Fact]
    public void A_very_long_sheet_name_is_shortened()
    {
        var name = OutputNames.SheetFile(new string('x', 250), FileFormat.Csv);

        Assert.Equal(104, name.Length);
        Assert.EndsWith(".csv", name);
    }

    [Fact]
    public void Slides_keep_their_own_numbers_in_their_names()
    {
        Assert.Equal("Slide 3.png", OutputNames.SlideFile(3, FileFormat.Png));
    }

    [Fact]
    public void Output_items_are_counted_in_their_own_unit()
    {
        Assert.Equal("1 slide", new Sulap.Core.Conversion.ConversionOutput("x", false, 1, 1, "slide").ItemsText);
        Assert.Equal("3 sheets", new Sulap.Core.Conversion.ConversionOutput("x", true, 1, 3, "sheet").ItemsText);
        Assert.Equal("2 pages", new Sulap.Core.Conversion.ConversionOutput("x", false, 1, 2).ItemsText);
    }

    [Fact]
    public void First_free_skips_files_and_folders()
    {
        File.WriteAllText(_folder.File("Report.jpg"), "taken");
        Directory.CreateDirectory(_folder.File("Report (2).jpg"));

        Assert.Equal(_folder.File("Report (3).jpg"), OutputNames.FirstFree(_folder.Path, "Report", ".jpg"));
    }

    [Fact]
    public async Task Writing_never_overwrites_and_leaves_no_temp_files()
    {
        File.WriteAllText(_folder.File("Report.txt"), "the original");

        var path = await OutputWriter.WriteFileAsync(_folder.Path, "Report", ".txt", WriteText("new"), CancellationToken.None);

        Assert.Equal(_folder.File("Report (2).txt"), path);
        Assert.Equal("the original", File.ReadAllText(_folder.File("Report.txt")));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(["Report (2).txt", "Report.txt"], _folder.Names());
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task A_failed_write_leaves_nothing_behind()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputWriter.WriteFileAsync(
            _folder.Path, "Broken", ".txt", (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Empty(_folder.Names());
    }

    [Fact]
    public async Task Folders_appear_complete_under_a_free_name()
    {
        Directory.CreateDirectory(_folder.File("Report (JPG)"));

        var path = await OutputWriter.WriteFolderAsync(
            _folder.Path,
            "Report (JPG)",
            async (temp, token) =>
            {
                await File.WriteAllTextAsync(Path.Combine(temp, "Page 1.jpg"), "1", token);
                await File.WriteAllTextAsync(Path.Combine(temp, "Page 2.jpg"), "2", token);
            },
            CancellationToken.None);

        Assert.Equal(_folder.File("Report (JPG) (2)"), path);
        Assert.Equal(["Page 1.jpg", "Page 2.jpg"], Directory.GetFiles(path).Select(Path.GetFileName).Order());
        Assert.Equal(["Report (JPG)", "Report (JPG) (2)"], _folder.Names());
    }

    [Fact]
    public async Task Writers_that_need_a_path_get_one_with_the_real_extension()
    {
        string? seen = null;
        var path = await OutputWriter.WriteFileByPathAsync(
            _folder.Path,
            "Doc",
            ".docx",
            (temp, token) =>
            {
                seen = temp;
                return File.WriteAllTextAsync(temp, "x", token);
            },
            CancellationToken.None);

        Assert.EndsWith(".docx", seen);
        Assert.Equal(_folder.File("Doc.docx"), path);
    }

    [Fact]
    public async Task A_folder_without_write_access_asks_for_another_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows ACLs.");
        var locked = new DirectoryInfo(_folder.File("locked"));
        locked.Create();
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            Assert.Throws<OutputFolderNotWritableException>(() => OutputWriter.EnsureWritable(locked.FullName));
            await Assert.ThrowsAsync<OutputFolderNotWritableException>(() =>
                OutputWriter.WriteFileAsync(locked.FullName, "Report", ".txt", WriteText("x"), CancellationToken.None));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    private static Func<Stream, CancellationToken, Task> WriteText(string text) =>
        (stream, token) => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token).AsTask();
}
