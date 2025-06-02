﻿using NadekoBot.Modules.Gambling.Services;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;
using Image = SixLabors.ImageSharp.Image;

namespace NadekoBot.Modules.Games;

public partial class Games
{
    [RequireContext(ContextType.Guild)]
    public sealed class NCanvasCommands : NadekoModule
    {
        private readonly INCanvasService _service;
        private readonly IHttpClientFactory _http;
        private readonly FontProvider _fonts;
        private readonly GamblingConfigService _gcs;

        public NCanvasCommands(
            INCanvasService service,
            IHttpClientFactory http,
            FontProvider fonts,
            GamblingConfigService gcs)
        {
            _service = service;
            _http = http;
            _fonts = fonts;
            _gcs = gcs;
        }

        [Cmd]
        public async Task NCanvas(kwum x)
            => await NcPixel(x);

        [Cmd]
        public async Task NCanvas()
        {
            var pixels = await _service.GetCanvas();
            var w = _service.GetWidth();
            var h = _service.GetHeight();
            var image = new Image<Rgba32>(w * 2, h * 2);

            Parallel.For(0,
                h * 2,
                y =>
                {
                    var pixelAccessor = image.DangerousGetPixelRowMemory(y);
                    var row = pixelAccessor.Span;
                    for (var x = 0; x < image.Width; x += 2)
                    {
                        var pi = pixels[(y / 2 * w) + x / 2];

                        row[x] = new Rgba32(pi);
                        row[x + 1] = new Rgba32(pi);
                    }
                });

            await using var stream = await image.ToStreamAsync();

            var hint = GetText(strs.nc_hint(prefix, _service.GetWidth(), _service.GetHeight()));

            var rng = new NadekoRandom();
            var inter = _inter.Create(ctx.User.Id,
                new ButtonBuilder("Zoom to a Random Spot",
                    Guid.NewGuid().ToString(),
                    ButtonStyle.Secondary),
                (_) => NCzoom(rng.Next(0, w), rng.Next(0, h)));

            await Response()
                .File(stream, "ncanvas.png")
                .Interaction(inter)
                .Embed(CreateEmbed()
                    .WithDescription("""
                                     Draw pixels on the canvas!
                                     `.ncz x y` to zoom in to particular coordinates
                                     `.ncs CODE color` to set a color
                                     `.nc` to see the whole canvas
                                     """)
                    .WithOkColor()
#if GLOBAL_NADEKO
                                .WithDescription("https://dashy.nadeko.bot/ncanvas")
#endif
                    .WithFooter(hint)
                    .WithImageUrl("attachment://ncanvas.png"))
                .SendAsync();
        }

        [Cmd]
        public Task NCzoom(int row, int col)
            => NCzoom((col * _service.GetWidth()) + row);

        [Cmd]
        public async Task NCzoom(kwum position)
        {
            var w = _service.GetWidth();
            var h = _service.GetHeight();

            if (position < 0 || position >= w * h)
            {
                await Response().Error(strs.invalid_input).SendAsync();
                return;
            }

            using var img = await GetZoomImage(position);
            await using var stream = await img.ToStreamAsync();
            var eb = CreateEmbed()
                .WithOkColor()
                .WithImageUrl($"attachment://zoom_{position}.png")
                .WithFooter($"`.ncs code color` to set | ex: `.ncs {position} pink`");

            await Response()
                .Embed(eb)
                .Interaction(_inter.Create(ctx.User.Id,
                    new ButtonBuilder("See Canvas",
                        Guid.NewGuid().ToString(),
                        ButtonStyle.Secondary),
                    async (smc) => await NCanvas()))
                .File(stream, $"zoom_{position}.png")
                .SendAsync();
        }

        private async Task<Image<Rgba32>> GetZoomImage(kwum position)
        {
            var w = _service.GetWidth();
            var pixels = await _service.GetPixelGroup(position);

            var origX = ((position % w) - 2) * 100;
            var origY = ((position / w) - 2) * 100;

            var image = new Image<Rgba32>(500, 500);

            const float fontSize = 30;

            var posFont = _fonts.NotoSans.CreateFont(fontSize, FontStyle.Bold);
            var priceFont = _fonts.Symbola.CreateFont(35, FontStyle.Regular);

            var size = TextMeasurer.MeasureSize("wwww", new TextOptions(posFont));
            var scale = 100f / size.Width;
            if (scale < 1)
                posFont = _fonts.NotoSans.CreateFont(fontSize * scale, FontStyle.Bold);
            var outlinePen = new SolidPen(SixLabors.ImageSharp.Color.Black, 0.5f);

            Parallel.For(0,
                pixels.Length,
                i =>
                {
                    var pix = pixels[i];
                    var startX = pix.Position % w * 100 - origX;
                    var startY = pix.Position / w * 100 - origY;

                    var color = new Rgba32(pix.Color);
                    image.Mutate(x => FillRectangleExtensions.Fill(x,
                        new SolidBrush(color),
                        new RectangleF(startX, startY, 100, 100)));

                    image.Mutate(x =>
                    {
                        x.DrawText(new RichTextOptions(posFont)
                            {
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                Origin = new(startX + 50, startY + 30)
                            },
                            ((kwum)pix.Position).ToString().PadLeft(2, '2'),
                            Brushes.Solid(SixLabors.ImageSharp.Color.White),
                            outlinePen);

                        x.DrawText(new RichTextOptions(priceFont)
                            {
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                Origin = new(startX + 50, startY + 80)
                            },
                            // "", Brushes.Solid(SixLabors.ImageSharp.Color.White), outlinePen);
                            pix.Price + "🌸",
                            // CurrencyHelper.N(pix.Price, Culture, _gcs.Data.Currency.Sign),
                            Brushes.Solid(SixLabors.ImageSharp.Color.White),
                            outlinePen);
                    });
                });

            // write the position on each section of the image
            return image;
        }

        [Cmd]
        public async Task NcSetPixel(kwum position, Rgba32 color, [Leftover] string text = "")
        {
            if (position < 0 || position >= _service.GetWidth() * _service.GetHeight())
            {
                await Response().Error(strs.invalid_input).SendAsync();
                return;
            }

            var pixel = await _service.GetPixel(position);
            if (pixel is null)
            {
                await Response().Error(strs.nc_pixel_not_found).SendAsync();
                return;
            }

            var prompt = GetText(strs.nc_pixel_set_confirm(Format.Code(position.ToString()),
                Format.Bold(CurrencyHelper.N(pixel.Price,
                    Culture,
                    _gcs.Data.Currency.Sign))));

            if (!await PromptUserConfirmAsync(CreateEmbed()
                    .WithPendingColor()
                    .WithDescription(prompt)))
            {
                return;
            }

            var result = await _service.SetPixel(position, color.PackedValue, text, ctx.User.Id, pixel.Price);

            if (result == SetPixelResult.NotEnoughMoney)
            {
                await Response().Error(strs.not_enough(_gcs.Data.Currency.Sign)).SendAsync();
                return;
            }
            else if (result == SetPixelResult.InsufficientPayment)
            {
                await Response().Error(strs.nc_insuff_payment).SendAsync();
                return;
            }
            else if (result == SetPixelResult.InvalidInput)
            {
                await Response().Error(strs.invalid_input).SendAsync();
                return;
            }

            using var img = await GetZoomImage(position);
            await using var stream = await img.ToStreamAsync();

            await Response()
                .Embed(CreateEmbed()
                    .WithOkColor()
                    .WithDescription(GetText(strs.nc_pixel_set(Format.Code(position.ToString()))))
                    .WithImageUrl($"attachment://zoom_{position}.png"))
                .Interaction(_inter.Create(ctx.User.Id,
                    new ButtonBuilder("See Canvas",
                        Guid.NewGuid().ToString(),
                        ButtonStyle.Secondary),
                    async (smc) => await NCanvas()))
                .File(stream, $"zoom_{position}.png")
                .SendAsync();
        }

        [Cmd]
        public async Task NcPixel(int x, int y)
            => await NcPixel((y * _service.GetWidth()) + x);

        [Cmd]
        public async Task NcPixel(kwum position)
        {
            if (position < 0 || position >= _service.GetWidth() * _service.GetHeight())
            {
                await Response().Error(strs.invalid_input).SendAsync();
                return;
            }

            var pixel = await _service.GetPixel(position);
            if (pixel is null)
            {
                await Response().Error(strs.nc_pixel_not_found).SendAsync();
                return;
            }

            var image = new Image<Rgba32>(100, 100);
            image.Mutate(x
                => x.Fill(new SolidBrush(new Rgba32(pixel.Color)),
                    new RectangleF(0, 0, 100, 100)));

            await using var stream = await image.ToStreamAsync();

            var pos = new kwum(pixel.Position);
            await Response()
                .File(stream, $"{pixel.Position}.png")
                .Embed(CreateEmbed()
                    .WithOkColor()
                    .WithDescription(string.IsNullOrWhiteSpace(pixel.Text) ? string.Empty : pixel.Text)
                    .WithTitle(GetText(strs.nc_pixel(pos)))
                    .AddField(GetText(strs.nc_position),
                        $"{pixel.Position % _service.GetWidth()} {pixel.Position / _service.GetWidth()}",
                        true)
                    .AddField(GetText(strs.price), pixel.Price.ToString(), true)
                    .AddField(GetText(strs.color), "#" + new Rgba32(pixel.Color).ToHex())
                    .WithImageUrl($"attachment://{pixel.Position}.png"))
                .SendAsync();
        }

        [Cmd]
        [OwnerOnly]
        public async Task NcSetImg()
        {
            var attach = ctx.Message.Attachments.FirstOrDefault();
            if (attach is null)
            {
                await Response().Error(strs.no_attach_found).SendAsync();
                return;
            }

            var w = _service.GetWidth();
            var h = _service.GetHeight();
            if (attach.Width != w || attach.Height != h)
            {
                await Response().Error(strs.invalid_img_size(w, h)).SendAsync();
                return;
            }

            if (!await PromptUserConfirmAsync(CreateEmbed()
                    .WithDescription(
                        "This will reset the canvas to the specified image. All prices, text and colors will be reset.\n\n"
                        + "Are you sure you want to continue?")))
                return;

            using var http = _http.CreateClient();
            await using var stream = await http.GetStreamAsync(attach.Url);
            using var img = await Image.LoadAsync<Rgba32>(stream);

            var pixels = new uint[_service.GetWidth() * _service.GetHeight()];

            Parallel.For(0,
                _service.GetWidth() * _service.GetHeight(),
                i => pixels[i] = img[i % _service.GetWidth(), i / _service.GetWidth()].PackedValue);

            // for (var y = 0; y < _service.GetHeight(); y++)
            // for (var x = 0; x < _service.GetWidth(); x++)
            // pixels[(y * _service.GetWidth()) + x] = img[x, y].PackedValue;

            await _service.SetImage(pixels);
            await ctx.OkAsync();
        }

        [Cmd]
        [OwnerOnly]
        public async Task NcReset()
        {
            await _service.ResetAsync();

            if (!await PromptUserConfirmAsync(CreateEmbed()
                    .WithDescription(
                        "This will delete all pixels and reset the canvas.\n\n"
                        + "Are you sure you want to continue?")))
                return;

            await ctx.OkAsync();
        }
    }
}