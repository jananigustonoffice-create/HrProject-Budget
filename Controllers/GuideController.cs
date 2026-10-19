using HrProject_Budget.Data;
using Microsoft.AspNetCore.Mvc;

namespace HrProject_Budget.Controllers;

/// <summary>User guide: the PDF (open / download / print) and training videos. Any signed-in user can watch; only admins add or remove videos.</summary>
public class GuideController(IWebHostEnvironment env, AccessService access, AuditService audit) : Controller
{
    const string FileName = "Cadre_Management_System_User_Guide.pdf";
    static readonly string[] VideoTypes = { ".mp4", ".webm", ".m4v" };
    string PdfPath => Path.Combine(env.ContentRootPath, "Guide", FileName);
    string VideoDir => Path.Combine(env.ContentRootPath, "Guide", "Videos");

    public record VideoItem(string File, string Title, long Size, DateTime Added);
    public record GuideVm(bool HasPdf, List<VideoItem> Videos);

    List<VideoItem> Videos() => !Directory.Exists(VideoDir) ? new() :
        new DirectoryInfo(VideoDir).GetFiles().Where(f => VideoTypes.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name).Select(f => new VideoItem(f.Name, Path.GetFileNameWithoutExtension(f.Name).Replace('_', ' '), f.Length, f.LastWriteTime)).ToList();

    public IActionResult Index() => View(new GuideVm(System.IO.File.Exists(PdfPath), Videos()));

    public IActionResult Open() => System.IO.File.Exists(PdfPath) ? PhysicalFile(PdfPath, "application/pdf") : NotFound();
    public IActionResult Download() => System.IO.File.Exists(PdfPath) ? PhysicalFile(PdfPath, "application/pdf", FileName) : NotFound();

    string? VideoPath(string? name)
    {
        var n = Path.GetFileName(name ?? "");
        if (n == "" || !VideoTypes.Contains(Path.GetExtension(n).ToLowerInvariant())) return null;
        var p = Path.Combine(VideoDir, n);
        return System.IO.File.Exists(p) ? p : null;
    }

    /// <summary>Streams a video (supports seeking).</summary>
    public IActionResult Video(string name)
    {
        var p = VideoPath(name);
        if (p == null) return NotFound();
        var ct = Path.GetExtension(p).ToLowerInvariant() == ".webm" ? "video/webm" : "video/mp4";
        return PhysicalFile(p, ct, enableRangeProcessing: true);
    }

    public IActionResult DownloadVideo(string name)
    {
        var p = VideoPath(name);
        if (p == null) return NotFound();
        return PhysicalFile(p, "application/octet-stream", Path.GetFileName(p), enableRangeProcessing: true);
    }

    [HttpPost, RequestSizeLimit(1_000_000_000), RequestFormLimits(MultipartBodyLengthLimit = 1_000_000_000)]
    public async Task<IActionResult> Upload(IFormFile? file, string? title)
    {
        if (!access.IsAdmin) return RedirectToAction("AccessDenied", "Account");
        if (file == null || file.Length == 0) { TempData["Error"] = "Choose a video file (.mp4 or .webm)."; return RedirectToAction(nameof(Index)); }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!VideoTypes.Contains(ext)) { TempData["Error"] = "Only .mp4, .m4v or .webm videos can be added."; return RedirectToAction(nameof(Index)); }
        // The file content must really be a video (MP4 'ftyp' box or WebM/EBML header), not just have a video name.
        var head = new byte[12];
        await using (var hs = file.OpenReadStream()) await hs.ReadExactlyAsync(head, 0, (int)Math.Min(12, file.Length));
        var isWebm = head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        var isMp4 = head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
        if (ext == ".webm" ? !isWebm : !isMp4) { TempData["Error"] = "The file is not a valid video."; return RedirectToAction(nameof(Index)); }
        var baseName = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, ' ');
        baseName = string.Join('_', baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (baseName.Length == 0) baseName = "Video";
        Directory.CreateDirectory(VideoDir);
        var path = Path.Combine(VideoDir, baseName + ext);
        await using (var fs = System.IO.File.Create(path)) await file.CopyToAsync(fs);
        await audit.LogAsync("Create", "Guide video", baseName + ext, $"Guide video '{baseName.Replace('_', ' ')}' added ({file.Length / 1048576.0:0.#} MB)");
        TempData["Ok"] = "Video added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteVideo(string name)
    {
        if (!access.IsAdmin) return RedirectToAction("AccessDenied", "Account");
        var p = VideoPath(name);
        if (p != null)
        {
            System.IO.File.Delete(p);
            await audit.LogAsync("Delete", "Guide video", Path.GetFileName(p), $"Guide video '{Path.GetFileNameWithoutExtension(p).Replace('_', ' ')}' removed");
            TempData["Ok"] = "Video removed.";
        }
        return RedirectToAction(nameof(Index));
    }
}
