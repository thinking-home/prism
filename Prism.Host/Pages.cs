using System.Net;
using System.Text;
using Prism.Host.Media;

namespace Prism.Host;

/// <summary>Формирует (на сервере) HTML для библиотеки и страницы плеера.</summary>
public static class Pages
{
    private const string Style = """
        <style>
          :root { color-scheme: dark; }
          body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
                 margin: 0; background: #0e0f13; color: #e7e9ee; }
          header { padding: 18px 24px; border-bottom: 1px solid #23252e; }
          header h1 { margin: 0; font-size: 18px; font-weight: 600; }
          header .sub { color: #8b90a0; font-size: 13px; margin-top: 4px; }
          main { padding: 24px; max-width: 1000px; margin: 0 auto; }
          a { color: #6ea8fe; text-decoration: none; }
          a:hover { text-decoration: underline; }
          .grid { display: grid; gap: 12px; }
          .card { background: #161821; border: 1px solid #23252e; border-radius: 10px;
                  padding: 14px 16px; display: flex; justify-content: space-between; align-items: center; gap: 16px; }
          .card .name { font-weight: 600; }
          .card .meta { color: #8b90a0; font-size: 13px; margin-top: 4px; }
          .badge { font-size: 12px; padding: 3px 9px; border-radius: 999px; white-space: nowrap; }
          .badge.direct { background: #143d2b; color: #4ade80; }
          .badge.transcode { background: #15324d; color: #6ea8fe; }
          .badge.unsupported { background: #422; color: #f87171; }
          video { width: 100%; max-height: 78vh; background: #000; border-radius: 10px; }
          .back { display: inline-block; margin-bottom: 16px; }
          .empty { color: #8b90a0; }
          code { background: #1c1f2a; padding: 2px 6px; border-radius: 5px; }
          .note { color: #8b90a0; font-size: 13px; margin-top: 12px; }
        </style>
        """;

    public static string Library(IReadOnlyList<MediaItem> items, string mediaDir, bool ffmpegAvailable)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Network Player</title>").Append(Style).Append("</head><body>");
        sb.Append("<header><h1>Network Player</h1>");
        sb.Append("<div class=\"sub\">Раздаётся <code>").Append(Enc(mediaDir)).Append("</code> &middot; ffmpeg: ")
          .Append(ffmpegAvailable ? "доступен" : "<span style=\"color:#f87171\">не найден</span>").Append("</div></header>");
        sb.Append("<main>");

        if (items.Count == 0)
        {
            sb.Append("<p class=\"empty\">Медиафайлы не найдены. Положите <code>.mkv</code> (или другое видео) ")
              .Append("в папку <code>").Append(Enc(mediaDir)).Append("</code> и обновите страницу.</p>");
        }
        else
        {
            sb.Append("<div class=\"grid\">");
            foreach (var it in items)
            {
                sb.Append("<div class=\"card\"><div>");
                sb.Append("<div class=\"name\"><a href=\"/watch/").Append(it.Id).Append("\">")
                  .Append(Enc(it.DisplayName)).Append("</a></div>");
                sb.Append("<div class=\"meta\">").Append(Enc(it.FileName)).Append("</div></div>");
                sb.Append("<a href=\"/watch/").Append(it.Id).Append("\">Смотреть &rarr;</a>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    public static string Watch(MediaItem item, string streamUrl, bool isHls)
    {
        var info = item.Info;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(Enc(item.DisplayName)).Append("</title>").Append(Style).Append("</head><body>");
        sb.Append("<header><h1>").Append(Enc(item.DisplayName)).Append("</h1>");
        if (info is not null)
        {
            sb.Append("<div class=\"sub\">");
            if (info.Width > 0) sb.Append(info.Width).Append('x').Append(info.Height).Append(" &middot; ");
            sb.Append("источник: ").Append(Enc(info.VideoCodec ?? "?"));
            if (info.HasAudio) sb.Append(" / ").Append(Enc(info.AudioCodec ?? "?"));
            sb.Append(" &middot; ").Append(isHls ? "транскодирование HLS" : "прямой поток");
            sb.Append("</div>");
        }
        sb.Append("</header><main>");
        sb.Append("<a class=\"back\" href=\"/\">&larr; Библиотека</a>");

        sb.Append("<video id=\"player\" controls autoplay playsinline></video>");
        sb.Append("<div class=\"note\">URL потока: <code>").Append(Enc(streamUrl)).Append("</code></div>");

        if (isHls)
        {
            // hls.js для Chrome/Firefox/Edge, нативный HLS для Safari.
            sb.Append("<script src=\"https://cdn.jsdelivr.net/npm/hls.js@1\"></script>");
            sb.Append("<script>(function(){var v=document.getElementById('player');var src=")
              .Append(JsString(streamUrl)).Append(";");
            sb.Append("if(window.Hls&&Hls.isSupported()){var h=new Hls({maxBufferLength:30});h.loadSource(src);h.attachMedia(v);}");
            sb.Append("else if(v.canPlayType('application/vnd.apple.mpegurl')){v.src=src;}");
            sb.Append("else{v.outerHTML='<p>Ваш браузер не умеет воспроизводить HLS.</p>';}})();</script>");
        }
        else
        {
            sb.Append("<script>(function(){var v=document.getElementById('player');v.src=")
              .Append(JsString(streamUrl)).Append(";})();</script>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    public static string Unsupported(MediaItem item)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(Enc(item.DisplayName)).Append("</title>").Append(Style).Append("</head><body>");
        sb.Append("<header><h1>").Append(Enc(item.DisplayName)).Append("</h1></header><main>");
        sb.Append("<a class=\"back\" href=\"/\">&larr; Библиотека</a>");
        sb.Append("<p><span class=\"badge unsupported\">воспроизведение невозможно</span></p>");
        sb.Append("<p>").Append(Enc(item.StatusMessage ?? "Этот файл нельзя воспроизвести в браузере.")).Append("</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string JsString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
