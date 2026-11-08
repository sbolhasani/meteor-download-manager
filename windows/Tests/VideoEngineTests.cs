using Nexonic.Core;
using Xunit;

namespace Nexonic.Tests;

/// <summary>The unpacked video engine's version checks and the Add window's quality menu.</summary>
public class VideoEngineTests
{
    [Theory]
    [InlineData("2026.10.26", "2026.10.25", true)]
    [InlineData("2026.10.26.1", "2026.10.26", true)]
    [InlineData("2026.10.26", "2026.10.26", false)]
    [InlineData("2026.9.30", "2026.10.1", false)]
    [InlineData("2027.1.1", "0", true)]
    public void Versions_compare_number_by_number(string a, string b, bool newer) =>
        Assert.Equal(newer, SiteVideoTools.Newer(a, b));

    [Fact]
    public void Checksum_is_read_for_the_right_file_only()
    {
        var sums = "aaa  yt-dlp.exe\nBBB  yt-dlp_win.zip\nccc  yt-dlp_win_x86.zip\n";
        Assert.Equal("bbb", SiteVideoTools.ChecksumFor("yt-dlp_win.zip", sums));
        Assert.Null(SiteVideoTools.ChecksumFor("yt-dlp_macos.zip", sums));
    }

    [Fact]
    public void Quality_menu_lists_best_lower_and_sound_only_with_sizes()
    {
        var choices = new SiteVideo.Choices("t", new List<SiteVideo.Quality> { new(1080, 900), new(720, 500), new(480, null) }, 40);
        var options = VideoPrefetch.Options(choices);
        Assert.Equal(new[] { 0, 720, 480, -1 }, options.Select(o => o.Tag));
        Assert.Equal("Sound only", options[^1].Label);
        Assert.StartsWith("Best (", options[0].Label);
        var info = new SiteVideo.Info("t", "mp4", 1000);
        Assert.Equal(1000, VideoPrefetch.SizeFor(info, choices, 0));
        Assert.Equal(500, VideoPrefetch.SizeFor(info, choices, 720));
        Assert.Equal(40, VideoPrefetch.SizeFor(info, choices, -1));
        Assert.Null(VideoPrefetch.SizeFor(info, choices, 480));
        Assert.Equal(new[] { 0, -1 }, VideoPrefetch.Options(null).Select(o => o.Tag));
    }

    [Fact]
    public void A_canceled_download_is_marked_as_such()
    {
        var r = new DownloadRecord { Status = DownloadStatus.Failed, Error = DownloadRecord.CanceledMessage };
        Assert.True(r.IsCanceled);
        r.Error = "Server error";
        Assert.False(r.IsCanceled);
    }
}


/// <summary>Audio-only HLS output must retain its codec and have an appropriate extension.</summary>
public class HlsAudioFormatTests
{
    [Theory]
    [InlineData("aac", ".m4a")]
    [InlineData("alac", ".m4a")]
    [InlineData("mp3", ".mp3")]
    [InlineData("opus", ".opus")]
    [InlineData("vorbis", ".ogg")]
    [InlineData("flac", ".flac")]
    [InlineData("unknown", ".mka")]
    public void Audio_extension_matches_codec_without_transcoding(string codec, string extension)
    {
        Assert.Equal(extension, new FFmpeg.MediaInfo(false, true, codec).AudioExtension);
    }

    [Fact]
    public void Video_and_audio_are_distinguished_by_streams_not_hls_track_label()
    {
        var audio = new FFmpeg.MediaInfo(false, true, "aac");
        var video = new FFmpeg.MediaInfo(true, true, "aac");
        Assert.False(audio.HasVideo);
        Assert.True(video.HasVideo);
    }
}

public class HlsPendingNameTests
{
    [Theory]
    [InlineData("Track.mp4", "Track")]
    [InlineData("master.m3u8", "master")]
    [InlineData("Track", "Track")]
    [InlineData("My.Track", "My.Track")]
    [InlineData("Track.m4a", "Track.m4a")]
    public void Hls_name_does_not_claim_mp4_before_probe(string input, string expected) =>
        Assert.Equal(expected, HLS.PendingFileName(input));
}

public class MediaFormatSelectionTests
{
    [Fact]
    public void Audio_only_choices_have_no_video_renditions()
    {
        var metadata = """{"title":"Sound","duration":10,"formats":[{"vcodec":"none","acodec":"mp4a.40.2","abr":160,"ext":"m4a"}]}""";
        var choices = SiteVideo.ParseChoices(metadata);
        Assert.NotNull(choices);
        Assert.Empty(choices.Video);
        Assert.NotNull(choices.SoundSize);
    }
}
