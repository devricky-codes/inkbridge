using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Inkbridge.Windows.Services;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using DragEventArgs = System.Windows.DragEventArgs;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Inkbridge.Windows;

public partial class WhiteboardWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly NetworkService _networkService;
    private double _zoom = 1.0;

    // Pan mode state
    private bool _isPanMode;
    private Point _panMouseStart;
    private double _panStartX, _panStartY;
    private double _panX, _panY;

    // Track shapes by id for resizing
    private readonly Dictionary<string, FrameworkElement> _shapeElements = new();

    // Chunked image receiving
    private readonly Dictionary<string, (double x, double y, double w, double h, int totalChunks, Dictionary<int, string> chunks)> _imageChunkBuffers = new();

    // Document mode state
    private bool _isDocMode;
    private string _docModeDir = "";
    private int _docModePage = 1;

    // Blank canvas tracking — scroll to first incoming stroke
    private bool _isBlankCanvas = true;

    // Overlay window reference for state sync
    private OverlayWindow? _overlayWindow;

    public WhiteboardWindow(NetworkService networkService)
    {
        InitializeComponent();
        _networkService = networkService;

        // Set window icon from file
        var icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        if (File.Exists(icoPath))
        {
            Icon = new BitmapImage(new Uri(icoPath, UriKind.Absolute));
        }

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };

        PreviewKeyDown += OnWindowKeyDown;
        // Initialize pan offset
        _panX = 0;
        _panY = 0;
        // Set initial transform
        CanvasTranslate.X = _panX;
        CanvasTranslate.Y = _panY;
    }

    public void HandleWhiteboardMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "wb-stroke")
            {
                var points = ParsePoints(doc.RootElement.GetProperty("points"));
                var width = doc.RootElement.TryGetProperty("width", out var wEl) ? wEl.GetDouble() : 4.0;
                var color = ParseColor(doc.RootElement, "color", 0xFFFFFFFF);
                DrawStroke(points, color, width, doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null);
                if (_isBlankCanvas && points.Count > 0)
                {
                    _isBlankCanvas = false;
                    var sx = points[0].X;
                    var sy = points[0].Y;
                    Dispatcher.BeginInvoke(() =>
                    {
                        ScrollHost.ScrollToHorizontalOffset(sx * _zoom - ScrollHost.ViewportWidth / 2);
                        ScrollHost.ScrollToVerticalOffset(sy * _zoom - ScrollHost.ViewportHeight / 2);
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            else if (type == "wb-shape")
            {
                HandleShape(doc.RootElement);
            }
            else if (type == "wb-erase")
            {
                var id = doc.RootElement.GetProperty("id").GetString();
                EraseById(id);
            }
            else if (type == "wb-clear")
            {
                WhiteboardCanvas.Children.Clear();
                _shapeElements.Clear();
                _isBlankCanvas = true;
            }
            else if (type == "wb-image")
            {
                HandleIncomingImage(doc.RootElement);
            }
            else if (type == "wb-image-begin")
            {
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var x = doc.RootElement.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 100;
                var y = doc.RootElement.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 100;
                var w = doc.RootElement.TryGetProperty("w", out var wEl2) ? wEl2.GetDouble() : 400;
                var h = doc.RootElement.TryGetProperty("h", out var hEl) ? hEl.GetDouble() : 300;
                var total = doc.RootElement.GetProperty("totalChunks").GetInt32();
                _imageChunkBuffers[id] = (x, y, w, h, total, new Dictionary<int, string>());
            }
            else if (type == "wb-image-chunk")
            {
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var index = doc.RootElement.GetProperty("index").GetInt32();
                var data = doc.RootElement.GetProperty("data").GetString()!;
                if (_imageChunkBuffers.TryGetValue(id, out var buf))
                    buf.chunks[index] = data;
            }
            else if (type == "wb-image-end")
            {
                var id = doc.RootElement.GetProperty("id").GetString()!;
                if (_imageChunkBuffers.Remove(id, out var buf))
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < buf.totalChunks; i++)
                        sb.Append(buf.chunks.GetValueOrDefault(i, ""));
                    var b64 = sb.ToString();
                    var bytes = Convert.FromBase64String(b64);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    EraseById(id);
                    var image = new Image { Source = bitmap, Width = buf.w, Height = buf.h, Stretch = Stretch.Fill, Tag = id };
                    Canvas.SetLeft(image, buf.x);
                    Canvas.SetTop(image, buf.y);
                    WhiteboardCanvas.Children.Add(image);
                    MakeDraggable(image);
                    MakeResizable(image);
                }
            }
            else if (type == "wb-resync-begin")
            {
                WhiteboardCanvas.Children.Clear();
                _shapeElements.Clear();
            }
            else if (type == "wb-doc-save")
            {
                var customName = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                Dispatcher.Invoke(() => SaveDocumentPage(customName));
            }
            else if (type == "wb-doc-next")
            {
                var customName = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                Dispatcher.Invoke(() => {
                    SaveDocumentPage(customName);
                    OnClear(null, null);
                    _docModePage++;
                });
            }
            else if (type == "wb-state-request")
            {
                // Tablet is requesting current PC state — send doc mode info
                Dispatcher.Invoke(BroadcastDocState);
            }
            else if (type == "wb-overlay-state-request")
            {
                // Tablet is requesting overlay state — trigger an immediate frame if overlay is open
                Dispatcher.Invoke(() => _overlayWindow?.RequestImmediateFrame());
            }
            // wb-resync-end is a no-op, strokes/shapes arrive between begin/end
        }
        catch { }
    }

    private List<Point> ParsePoints(JsonElement pointsEl)
    {
        var points = new List<Point>();
        foreach (var pt in pointsEl.EnumerateArray())
            points.Add(new Point(pt.GetProperty("x").GetDouble(), pt.GetProperty("y").GetDouble()));
        return points;
    }

    private Color ParseColor(JsonElement el, string prop, ulong fallback)
    {
        ulong val_ = el.TryGetProperty(prop, out var cEl) ? (ulong)cEl.GetInt64() : fallback;
        return Color.FromArgb((byte)((val_ >> 24) & 0xFF), (byte)((val_ >> 16) & 0xFF),
            (byte)((val_ >> 8) & 0xFF), (byte)(val_ & 0xFF));
    }

    private void DrawStroke(List<Point> points, Color color, double width, string? id)
    {
        if (points.Count < 2) return;
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = id
        };
        foreach (var pt in points) polyline.Points.Add(pt);
        WhiteboardCanvas.Children.Add(polyline);
    }

    private void HandleShape(JsonElement el)
    {
        var id = el.GetProperty("id").GetString()!;
        var kind = el.GetProperty("kind").GetString();
        var x1 = el.GetProperty("x1").GetDouble();
        var y1 = el.GetProperty("y1").GetDouble();
        var x2 = el.GetProperty("x2").GetDouble();
        var y2 = el.GetProperty("y2").GetDouble();
        var strokeColor = ParseColor(el, "strokeColor", 0xFFFFFFFF);
        var fillColorVal = el.TryGetProperty("fillColor", out var fcEl) ? (ulong)fcEl.GetInt64() : 0;
        var fillColor = fillColorVal == 0
            ? System.Windows.Media.Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb((byte)((fillColorVal >> 24) & 0xFF), (byte)((fillColorVal >> 16) & 0xFF),
                (byte)((fillColorVal >> 8) & 0xFF), (byte)(fillColorVal & 0xFF)));
        var sw = el.TryGetProperty("strokeWidth", out var swEl) ? swEl.GetDouble() : 4.0;

        // Remove old version if exists
        EraseById(id);

        FrameworkElement shape;
        switch (kind)
        {
            case "rect":
                var rect = new Rectangle
                {
                    Width = Math.Abs(x2 - x1),
                    Height = Math.Abs(y2 - y1),
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = sw,
                    Fill = fillColor,
                    Tag = id
                };
                Canvas.SetLeft(rect, Math.Min(x1, x2));
                Canvas.SetTop(rect, Math.Min(y1, y2));
                shape = rect;
                break;
            case "circle":
                var ell = new Ellipse
                {
                    Width = Math.Abs(x2 - x1),
                    Height = Math.Abs(y2 - y1),
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = sw,
                    Fill = fillColor,
                    Tag = id
                };
                Canvas.SetLeft(ell, Math.Min(x1, x2));
                Canvas.SetTop(ell, Math.Min(y1, y2));
                shape = ell;
                break;
            case "line":
            default:
                var line = new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = sw,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Tag = id
                };
                shape = line;
                break;
        }

        WhiteboardCanvas.Children.Add(shape);
        _shapeElements[id] = shape;

        // Make shapes draggable and resizable on PC
        MakeDraggable(shape);
        MakeResizable(shape);
    }

    private void EraseById(string? id)
    {
        if (id == null) return;
        var toRemove = WhiteboardCanvas.Children.OfType<FrameworkElement>().Where(e => (e.Tag as string) == id).ToList();
        foreach (var el in toRemove) WhiteboardCanvas.Children.Remove(el);
        _shapeElements.Remove(id);
    }

    private void HandleIncomingImage(JsonElement el)
    {
        try
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : $"img_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var x = el.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 100;
            var y = el.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 100;
            var w = el.TryGetProperty("w", out var wEl) ? wEl.GetDouble() : 400;
            var h = el.TryGetProperty("h", out var hEl) ? hEl.GetDouble() : 300;
            var data = el.GetProperty("data").GetString();

            var bytes = Convert.FromBase64String(data!);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            EraseById(id);

            var image = new Image
            {
                Source = bitmap,
                Width = w,
                Height = h,
                Stretch = Stretch.Fill,
                Tag = id
            };
            Canvas.SetLeft(image, x);
            Canvas.SetTop(image, y);
            WhiteboardCanvas.Children.Add(image);
            MakeDraggable(image);
            MakeResizable(image);
        }
        catch { }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                PasteImageFromClipboard();
                e.Handled = true;
            }
        }
    }

    private void PasteImageFromClipboard()
    {
        var bitmapSource = System.Windows.Clipboard.GetImage();
        if (bitmapSource == null) return;

        // Encode to PNG bytes
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();

        if (bytes.Length > 10 * 1024 * 1024)
        {
            System.Windows.MessageBox.Show(
                $"Pasted image is too large ({bytes.Length / (1024 * 1024)}MB).\nMaximum allowed size is 10MB.",
                "Image Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        var w = Math.Min(bitmap.PixelWidth, 600.0);
        var h = w * bitmap.PixelHeight / bitmap.PixelWidth;

        var id = $"img_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000)}";

        // Place near the center of the current viewport
        var x = ScrollHost.HorizontalOffset / _zoom + ScrollHost.ViewportWidth / _zoom / 2 - w / 2;
        var y = ScrollHost.VerticalOffset / _zoom + ScrollHost.ViewportHeight / _zoom / 2 - h / 2;

        var image = new Image
        {
            Source = bitmap,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            Tag = id
        };

        Canvas.SetLeft(image, x);
        Canvas.SetTop(image, y);
        WhiteboardCanvas.Children.Add(image);

        MakeDraggable(image);
        MakeResizable(image);

        // Compress and send to tablet
        SendBitmapToTablet(bitmapSource, id, x, y, w, h);
    }

    private async void SendBitmapToTablet(BitmapSource bitmapSource, string id, double x, double y, double w, double h)
    {
        try
        {
            SendingOverlay.Visibility = Visibility.Visible;
            SendingLabel.Text = "Sending image...";
            SendingProgress.Value = 0;

            // Compress to JPEG, resize if needed
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            if (bitmapSource.PixelWidth > 1600)
            {
                var scale = 1600.0 / bitmapSource.PixelWidth;
                var tb = new TransformedBitmap(bitmapSource, new ScaleTransform(scale, scale));
                encoder.Frames.Add(BitmapFrame.Create(tb));
            }
            else
            {
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            }

            using var ms = new MemoryStream();
            encoder.Save(ms);
            var b64 = Convert.ToBase64String(ms.ToArray());

            await SendBase64ImageToTablet(id, x, y, w, h, b64);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SendBitmapToTablet error: {ex.Message}");
            SendingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAddImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
        if (dlg.ShowDialog() == true)
        {
            AddImageToCanvas(dlg.FileName, 100, 100);
        }
    }

    private void AddImageToCanvas(string filePath, double x, double y)
    {
        try
        {
            // Check file size — reject if larger than 10MB
            var fileInfo = new System.IO.FileInfo(filePath);
            if (fileInfo.Length > 10 * 1024 * 1024)
            {
                System.Windows.MessageBox.Show(
                    $"Image is too large ({fileInfo.Length / (1024 * 1024)}MB).\nMaximum allowed size is 10MB.",
                    "Image Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var w = Math.Min(bitmap.PixelWidth, 600.0);
            var h = w * bitmap.PixelHeight / bitmap.PixelWidth;

            var image = new Image
            {
                Source = bitmap,
                Width = w,
                Height = h,
                Stretch = Stretch.Fill
            };

            var id = $"img_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000)}";
            image.Tag = id;

            Canvas.SetLeft(image, x);
            Canvas.SetTop(image, y);
            WhiteboardCanvas.Children.Add(image);

            MakeDraggable(image);
            MakeResizable(image);

            // Send image to tablet
            SendImageToTablet(filePath, id, x, y, w, h);
        }
        catch { }
    }

    private async void SendImageToTablet(string filePath, string id, double x, double y, double w, double h)
    {
        try
        {
            // Show overlay
            SendingOverlay.Visibility = Visibility.Visible;
            SendingLabel.Text = "Sending image...";
            SendingProgress.Value = 0;

            // Compress image to JPEG for smaller payload
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            // Resize if too large (max 1600px wide)
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            if (bitmap.PixelWidth > 1600)
            {
                var scale = 1600.0 / bitmap.PixelWidth;
                var tb = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                encoder.Frames.Add(BitmapFrame.Create(tb));
            }
            else
            {
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
            }

            using var ms = new MemoryStream();
            encoder.Save(ms);
            var b64 = Convert.ToBase64String(ms.ToArray());

            // Chunk the base64 data (200KB chunks)
            const int chunkSize = 200_000;
            var totalChunks = (int)Math.Ceiling((double)b64.Length / chunkSize);

            // Send begin message
            var beginMsg = JsonSerializer.Serialize(new
            {
                type = "wb-image-begin",
                id, x, y, w, h,
                totalChunks
            });
            await _networkService.BroadcastJsonAsync(beginMsg);

            // Send chunks
            for (int i = 0; i < totalChunks; i++)
            {
                var start = i * chunkSize;
                var length = Math.Min(chunkSize, b64.Length - start);
                var chunkData = b64.Substring(start, length);

                var chunkMsg = JsonSerializer.Serialize(new
                {
                    type = "wb-image-chunk",
                    id,
                    index = i,
                    data = chunkData
                });
                await _networkService.BroadcastJsonAsync(chunkMsg);

                // Update progress
                SendingProgress.Value = (int)((i + 1) * 100.0 / totalChunks);
                SendingLabel.Text = $"Sending image... {i + 1}/{totalChunks}";

                // Yield to UI thread
                await Task.Delay(10);
            }

            // Send end message
            var endMsg = JsonSerializer.Serialize(new { type = "wb-image-end", id });
            await _networkService.BroadcastJsonAsync(endMsg);

            SendingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SendImageToTablet error: {ex.Message}");
            SendingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAddLink(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "Add Link",
            Width = 420, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            ResizeMode = ResizeMode.NoResize
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        var tb = new TextBox { Text = "https://", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), FontSize = 14, Padding = new Thickness(6) };
        var btn = new Button { Content = "Add", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        string? result = null;
        btn.Click += (s2, e2) => { result = tb.Text; dlg.Close(); };
        sp.Children.Add(tb);
        sp.Children.Add(btn);
        dlg.Content = sp;
        dlg.ShowDialog();

        if (!string.IsNullOrWhiteSpace(result))
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Child = new TextBlock
                {
                    Text = result,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x99, 0xFF)),
                    FontSize = 14,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = Cursors.Hand
                }
            };

            border.MouseLeftButtonUp += (s, args) =>
            {
                if (!_isDragging)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result) { UseShellExecute = true }); } catch { }
                }
            };

            Canvas.SetLeft(border, 200);
            Canvas.SetTop(border, 200);
            WhiteboardCanvas.Children.Add(border);
            MakeDraggable(border);
        }
    }

    private void SaveWhiteboardToFile(string filePath)
    {
        var elements = new List<Dictionary<string, object>>();

        foreach (var child in WhiteboardCanvas.Children.OfType<FrameworkElement>())
        {
            if (child is Polyline poly)
            {
                var pts = poly.Points.Select(p => new { x = p.X, y = p.Y }).ToList();
                var sc = (poly.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "stroke",
                    ["id"] = (child.Tag as string) ?? "",
                    ["points"] = pts,
                    ["color"] = ((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B,
                    ["width"] = poly.StrokeThickness
                });
            }
            else if (child is Rectangle rect)
            {
                var sc = (rect.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                var fc = (rect.Fill as SolidColorBrush)?.Color ?? Colors.Transparent;
                var x1 = Canvas.GetLeft(rect); var y1 = Canvas.GetTop(rect);
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "shape", ["kind"] = "rect",
                    ["id"] = (child.Tag as string) ?? "",
                    ["x1"] = x1, ["y1"] = y1, ["x2"] = x1 + rect.Width, ["y2"] = y1 + rect.Height,
                    ["strokeColor"] = ((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B,
                    ["fillColor"] = ((ulong)fc.A << 24) | ((ulong)fc.R << 16) | ((ulong)fc.G << 8) | fc.B,
                    ["strokeWidth"] = rect.StrokeThickness
                });
            }
            else if (child is Ellipse ell)
            {
                var sc = (ell.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                var fc = (ell.Fill as SolidColorBrush)?.Color ?? Colors.Transparent;
                var x1 = Canvas.GetLeft(ell); var y1 = Canvas.GetTop(ell);
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "shape", ["kind"] = "circle",
                    ["id"] = (child.Tag as string) ?? "",
                    ["x1"] = x1, ["y1"] = y1, ["x2"] = x1 + ell.Width, ["y2"] = y1 + ell.Height,
                    ["strokeColor"] = ((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B,
                    ["fillColor"] = ((ulong)fc.A << 24) | ((ulong)fc.R << 16) | ((ulong)fc.G << 8) | fc.B,
                    ["strokeWidth"] = ell.StrokeThickness
                });
            }
            else if (child is Line line)
            {
                var sc = (line.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "shape", ["kind"] = "line",
                    ["id"] = (child.Tag as string) ?? "",
                    ["x1"] = line.X1, ["y1"] = line.Y1, ["x2"] = line.X2, ["y2"] = line.Y2,
                    ["strokeColor"] = ((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B,
                    ["fillColor"] = (ulong)0,
                    ["strokeWidth"] = line.StrokeThickness
                });
            }
            else if (child is Image img && img.Source is BitmapSource bmp)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var b64 = Convert.ToBase64String(ms.ToArray());
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "image",
                    ["id"] = (child.Tag as string) ?? "",
                    ["x"] = Canvas.GetLeft(img),
                    ["y"] = Canvas.GetTop(img),
                    ["w"] = img.Width,
                    ["h"] = img.Height,
                    ["data"] = b64
                });
            }
            else if (child is Border border && border.Child is TextBlock tb)
            {
                elements.Add(new Dictionary<string, object>
                {
                    ["elementType"] = "link",
                    ["url"] = tb.Text,
                    ["x"] = Canvas.GetLeft(border),
                    ["y"] = Canvas.GetTop(border)
                });
            }
        }

        var json = JsonSerializer.Serialize(elements, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Inkbridge Whiteboard|*.inkboard",
            DefaultExt = ".inkboard",
            FileName = $"whiteboard_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;
        SaveWhiteboardToFile(dlg.FileName);
    }

    // --- Document Mode ---
    private void OnDocModeToggle(object sender, RoutedEventArgs e)
    {
        if (DocModeToggle.IsChecked == true)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _docModeDir = dialog.SelectedPath;
                _docModePage = 1;
                _isDocMode = true;
                DocUI.Visibility = Visibility.Visible;
                DocModePathText.Text = $"📁 Document Mode Active: {_docModeDir}";
                DocModePathText.Visibility = Visibility.Visible;
                BroadcastDocState();
            }
            else
            {
                DocModeToggle.IsChecked = false;
            }
        }
        else
        {
            _isDocMode = false;
            DocUI.Visibility = Visibility.Collapsed;
            DocModePathText.Visibility = Visibility.Collapsed;
            BroadcastDocState();
        }
    }

    private void BroadcastDocState()
    {
        var msg = JsonSerializer.Serialize(new { type = "wb-doc-state", active = _isDocMode, dir = Path.GetFileName(_docModeDir) });
        _ = _networkService.BroadcastJsonAsync(msg);
    }

    private void SaveDocumentPage(string? customName)
    {
        if (!_isDocMode || string.IsNullOrWhiteSpace(_docModeDir)) return;
        var name = string.IsNullOrWhiteSpace(customName)
            ? $"{Path.GetFileName(_docModeDir)}_Page_{_docModePage}.inkboard"
            : (customName.EndsWith(".inkboard") ? customName : customName + ".inkboard");
        var path = Path.Combine(_docModeDir, name);

        if (File.Exists(path))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            name = $"{nameWithoutExt}_{timestamp}.inkboard";
            path = Path.Combine(_docModeDir, name);
        }

        SaveWhiteboardToFile(path);
    }

    private void OnDocSave(object sender, RoutedEventArgs e)
    {
        SaveDocumentPage(null);
        System.Windows.MessageBox.Show($"Saved Page {_docModePage}.", "Document Mode", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDocNext(object sender, RoutedEventArgs e)
    {
        SaveDocumentPage(null);
        OnClear(null, null);
        _docModePage++;
    }

    private void OnOpenOverlay(object sender, RoutedEventArgs e)
    {
        _overlayWindow = new OverlayWindow(_networkService);
        _overlayWindow.Closed += (s, args) => _overlayWindow = null;
        _overlayWindow.Show();
        _overlayWindow.Activate();
    }

    public async Task LoadWhiteboardFromFileAsync(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            await LoadWhiteboardFromJsonAsync(json);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to load whiteboard: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLoad(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Inkbridge Whiteboard|*.inkboard" };
        if (dlg.ShowDialog() != true) return;
        await LoadWhiteboardFromFileAsync(dlg.FileName);
    }

    private async Task LoadWhiteboardFromJsonAsync(string json)
    {
        // Parse into a list of raw element dictionaries so we don't hold a JsonDocument across awaits
        var elements = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new List<JsonElement>();

        WhiteboardCanvas.Children.Clear();
        _shapeElements.Clear();

        // Clear tablet and give it time to process
        await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-clear" }));
        await Task.Delay(100);

        int zIndex = 0;
        foreach (var el in elements)
        {
            var elType = el.GetProperty("elementType").GetString();
            switch (elType)
            {
                case "stroke":
                {
                    var points = ParsePoints(el.GetProperty("points"));
                    var color = ParseColor(el, "color", 0xFFFFFFFF);
                    var width = el.TryGetProperty("width", out var wEl) ? wEl.GetDouble() : 4.0;
                    var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    int cntBefore = WhiteboardCanvas.Children.Count;
                    DrawStroke(points, color, width, id);
                    if (WhiteboardCanvas.Children.Count > cntBefore)
                        Canvas.SetZIndex(WhiteboardCanvas.Children[WhiteboardCanvas.Children.Count - 1], zIndex);
                    zIndex++;
                    long colorVal = (long)(((ulong)color.A << 24) | ((ulong)color.R << 16) | ((ulong)color.G << 8) | color.B);
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-stroke", id,
                        points = points.Select(p => new { x = p.X, y = p.Y }),
                        color = colorVal, width
                    }));
                    await Task.Delay(10);
                    break;
                }
                case "shape":
                {
                    int cntBefore = WhiteboardCanvas.Children.Count;
                    HandleShape(el);
                    if (WhiteboardCanvas.Children.Count > cntBefore)
                        Canvas.SetZIndex(WhiteboardCanvas.Children[WhiteboardCanvas.Children.Count - 1], zIndex);
                    zIndex++;
                    var shapeId = el.TryGetProperty("id", out var sidEl) ? sidEl.GetString() ?? "" : "";
                    var kind = el.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "rect" : "rect";
                    var sx1 = el.TryGetProperty("x1", out var x1El) ? x1El.GetDouble() : 0;
                    var sy1 = el.TryGetProperty("y1", out var y1El) ? y1El.GetDouble() : 0;
                    var sx2 = el.TryGetProperty("x2", out var x2El) ? x2El.GetDouble() : 100;
                    var sy2 = el.TryGetProperty("y2", out var y2El) ? y2El.GetDouble() : 100;
                    long sc = el.TryGetProperty("strokeColor", out var scEl) ? scEl.GetInt64() : unchecked((long)0xFFFFFFFF);
                    long fc = el.TryGetProperty("fillColor", out var fcEl) ? fcEl.GetInt64() : 0L;
                    var sw = el.TryGetProperty("strokeWidth", out var swEl) ? swEl.GetDouble() : 4.0;
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-shape", id = shapeId, kind,
                        x1 = sx1, y1 = sy1, x2 = sx2, y2 = sy2,
                        strokeColor = sc, fillColor = fc, strokeWidth = sw
                    }));
                    await Task.Delay(10);
                    break;
                }
                case "image":
                {
                    int cntBefore = WhiteboardCanvas.Children.Count;
                    HandleIncomingImage(el);
                    if (WhiteboardCanvas.Children.Count > cntBefore)
                        Canvas.SetZIndex(WhiteboardCanvas.Children[WhiteboardCanvas.Children.Count - 1], zIndex);
                    zIndex++;
                    var imgData = el.TryGetProperty("data", out var dEl) ? dEl.GetString() ?? "" : "";
                    var imgId = el.TryGetProperty("id", out var iid) ? iid.GetString() ?? "" : "";
                    var imgX = el.TryGetProperty("x", out var ix) ? ix.GetDouble() : 100;
                    var imgY = el.TryGetProperty("y", out var iy) ? iy.GetDouble() : 100;
                    var imgW = el.TryGetProperty("w", out var iw) ? iw.GetDouble() : 400;
                    var imgH = el.TryGetProperty("h", out var ih) ? ih.GetDouble() : 300;
                    if (!string.IsNullOrEmpty(imgData))
                        await SendBase64ImageToTablet(imgId, imgX, imgY, imgW, imgH, imgData);
                    break;
                }
                case "link":
                {
                    var url = el.GetProperty("url").GetString() ?? "";
                    var x = el.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 200;
                    var y = el.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 200;
                    var border = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8),
                        Child = new TextBlock
                        {
                            Text = url,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x99, 0xFF)),
                            FontSize = 14,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = Cursors.Hand
                        }
                    };
                    var capturedUrl = url;
                    border.MouseLeftButtonUp += (s, args) =>
                    {
                        if (!_isDragging)
                        {
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedUrl) { UseShellExecute = true }); } catch { }
                        }
                    };
                    Canvas.SetLeft(border, x);
                    Canvas.SetTop(border, y);
                    WhiteboardCanvas.Children.Add(border);
                    Canvas.SetZIndex(border, zIndex);
                    zIndex++;
                    MakeDraggable(border);
                    break;
                }
            }
        }
    }

    private async Task SendBase64ImageToTablet(string id, double x, double y, double w, double h, string b64)
    {
        const int chunkSize = 200_000;
        var totalChunks = (int)Math.Ceiling((double)b64.Length / chunkSize);

        SendingOverlay.Visibility = Visibility.Visible;
        SendingLabel.Text = "Sending image...";
        SendingProgress.Value = 0;

        await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-image-begin", id, x, y, w, h, totalChunks }));

        for (int i = 0; i < totalChunks; i++)
        {
            var start = i * chunkSize;
            var length = Math.Min(chunkSize, b64.Length - start);
            var chunkData = b64.Substring(start, length);
            await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-image-chunk", id, index = i, data = chunkData }));
            SendingProgress.Value = (int)((i + 1) * 100.0 / totalChunks);
            SendingLabel.Text = $"Sending image... {i + 1}/{totalChunks}";
            await Task.Delay(10);
        }

        await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-image-end", id }));
        SendingOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnFocusFirst(object sender, RoutedEventArgs e)
    {
        try
        {
            // Find the topmost-left stroke or element on the canvas
            double targetX = double.NaN, targetY = double.NaN;

            foreach (var child in WhiteboardCanvas.Children.OfType<FrameworkElement>())
            {
                double cx = double.NaN, cy = double.NaN;
                if (child is Polyline poly && poly.Points.Count > 0)
                {
                    cx = poly.Points[0].X;
                    cy = poly.Points[0].Y;
                }
                else if (child is Line ln)
                {
                    cx = Math.Min(ln.X1, ln.X2);
                    cy = Math.Min(ln.Y1, ln.Y2);
                }
                else if (child is Rectangle or Ellipse || child is Image || child is Border)
                {
                    cx = Canvas.GetLeft(child);
                    cy = Canvas.GetTop(child);
                    // Skip elements with no explicit canvas position
                    if (double.IsNaN(cx) || double.IsNaN(cy)) { cx = double.NaN; cy = double.NaN; }
                }

                if (!double.IsNaN(cx) && !double.IsNaN(cy))
                {
                    if (double.IsNaN(targetX) || cy < targetY || (Math.Abs(cy - targetY) < 1 && cx < targetX))
                    {
                        targetX = cx;
                        targetY = cy;
                    }
                }
            }

            if (!double.IsNaN(targetX))
            {
                // Adjust pan transform so the target point is centred in the viewport.
                // Screen position of a canvas point = point * zoom + pan - scrollOffset.
                // We want that to equal viewportSize/2, so:
                //   pan = viewportSize/2 + scrollOffset - point * zoom
                _panX = ScrollHost.ViewportWidth / 2 + ScrollHost.HorizontalOffset - targetX * _zoom;
                _panY = ScrollHost.ViewportHeight / 2 + ScrollHost.VerticalOffset - targetY * _zoom;
                CanvasTranslate.X = _panX;
                CanvasTranslate.Y = _panY;
            }
            else
            {
                System.Windows.MessageBox.Show("No content found on the canvas to focus on.", "Focus", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Focus failed: {ex.Message}", "Focus Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        WhiteboardCanvas.Children.Clear();
        _shapeElements.Clear();
        _isBlankCanvas = true;
        var msg = JsonSerializer.Serialize(new { type = "wb-clear" });
        _ = _networkService.BroadcastJsonAsync(msg);
    }

    private async void OnSyncToTablet(object sender, RoutedEventArgs e)
    {
        // Clear tablet first
        await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-clear" }));
        await Task.Delay(100);

        foreach (var child in WhiteboardCanvas.Children.OfType<FrameworkElement>().ToList())
        {
            try
            {
                if (child is Polyline poly)
                {
                    var sc = (poly.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                    long colorVal = (long)(((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B);
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-stroke",
                        id = (child.Tag as string) ?? "",
                        points = poly.Points.Select(p => new { x = p.X, y = p.Y }),
                        color = colorVal,
                        width = poly.StrokeThickness
                    }));
                    await Task.Delay(10);
                }
                else if (child is Rectangle rect)
                {
                    var sc = (rect.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                    var fc = (rect.Fill as SolidColorBrush)?.Color ?? Colors.Transparent;
                    var x1 = Canvas.GetLeft(rect); var y1 = Canvas.GetTop(rect);
                    long strokeC = (long)(((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B);
                    long fillC = (long)(((ulong)fc.A << 24) | ((ulong)fc.R << 16) | ((ulong)fc.G << 8) | fc.B);
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-shape", id = (child.Tag as string) ?? "", kind = "rect",
                        x1, y1, x2 = x1 + rect.Width, y2 = y1 + rect.Height,
                        strokeColor = strokeC, fillColor = fillC, strokeWidth = rect.StrokeThickness
                    }));
                    await Task.Delay(10);
                }
                else if (child is Ellipse ell)
                {
                    var sc = (ell.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                    var fc = (ell.Fill as SolidColorBrush)?.Color ?? Colors.Transparent;
                    var x1 = Canvas.GetLeft(ell); var y1 = Canvas.GetTop(ell);
                    long strokeC = (long)(((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B);
                    long fillC = (long)(((ulong)fc.A << 24) | ((ulong)fc.R << 16) | ((ulong)fc.G << 8) | fc.B);
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-shape", id = (child.Tag as string) ?? "", kind = "circle",
                        x1, y1, x2 = x1 + ell.Width, y2 = y1 + ell.Height,
                        strokeColor = strokeC, fillColor = fillC, strokeWidth = ell.StrokeThickness
                    }));
                    await Task.Delay(10);
                }
                else if (child is Line line)
                {
                    var sc = (line.Stroke as SolidColorBrush)?.Color ?? Colors.White;
                    long strokeC = (long)(((ulong)sc.A << 24) | ((ulong)sc.R << 16) | ((ulong)sc.G << 8) | sc.B);
                    await _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new {
                        type = "wb-shape", id = (child.Tag as string) ?? "", kind = "line",
                        x1 = line.X1, y1 = line.Y1, x2 = line.X2, y2 = line.Y2,
                        strokeColor = strokeC, fillColor = 0L, strokeWidth = line.StrokeThickness
                    }));
                    await Task.Delay(10);
                }
                else if (child is Image img && img.Source is BitmapSource bmp)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    var b64 = Convert.ToBase64String(ms.ToArray());
                    var imgId = (child.Tag as string) ?? "";
                    await SendBase64ImageToTablet(imgId, Canvas.GetLeft(img), Canvas.GetTop(img), img.Width, img.Height, b64);
                }
            }
            catch { }
        }

        // Always re-send doc mode state so tablet is in sync
        BroadcastDocState();

        // If overlay is open, send an immediate frame so tablet overlay tab gets the current state
        _overlayWindow?.RequestImmediateFrame();
    }

    // Drag support
    private bool _isDragging;
    private Point _dragStart;
    private double _dragOrigX, _dragOrigY;
    private UIElement? _dragTarget;

    private void MakeDraggable(UIElement element)
    {
        // Right-click context menu to remove
        var ctxMenu = new ContextMenu();
        var removeItem = new MenuItem { Header = "Remove" };
        removeItem.Click += (s, e) =>
        {
            var id = (element as FrameworkElement)?.Tag as string;
            WhiteboardCanvas.Children.Remove(element);
            if (id != null)
            {
                _shapeElements.Remove(id);
                var msg = JsonSerializer.Serialize(new { type = "wb-erase", id });
                _ = _networkService.BroadcastJsonAsync(msg);
            }
        };
        ctxMenu.Items.Add(removeItem);
        element.SetValue(FrameworkElement.ContextMenuProperty, ctxMenu);

        element.MouseLeftButtonDown += (s, e) =>
        {
            _isDragging = false;
            _dragTarget = element;
            _dragStart = e.GetPosition(WhiteboardCanvas);
            _dragOrigX = Canvas.GetLeft(element);
            _dragOrigY = Canvas.GetTop(element);
            if (double.IsNaN(_dragOrigX)) _dragOrigX = 0;
            if (double.IsNaN(_dragOrigY)) _dragOrigY = 0;
            element.CaptureMouse();
            e.Handled = true;
        };

        element.MouseMove += (s, e) =>
        {
            if (_dragTarget == element && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(WhiteboardCanvas);
                var dx = pos.X - _dragStart.X;
                var dy = pos.Y - _dragStart.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
                    _isDragging = true;
                Canvas.SetLeft(element, _dragOrigX + dx);
                Canvas.SetTop(element, _dragOrigY + dy);
            }
        };

        element.MouseLeftButtonUp += (s, e) =>
        {
            if (_dragTarget == element)
            {
                element.ReleaseMouseCapture();
                if (_isDragging)
                    SyncElementPosition(element);
                _dragTarget = null;
            }
        };
    }

    private void MakeResizable(FrameworkElement element)
    {
        element.MouseWheel += (s, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt)
            {
                var factor = e.Delta > 0 ? 1.1 : 0.9;
                element.Width = Math.Max(20, Math.Min(3000, element.Width * factor));
                if (!double.IsNaN(element.Height))
                    element.Height = Math.Max(20, Math.Min(3000, element.Height * factor));
                e.Handled = true;
                SyncElementPosition(element);
            }
        };
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        var bounds = Rect.Empty;
        foreach (UIElement child in WhiteboardCanvas.Children)
        {
            if (child is FrameworkElement fe)
            {
                var x = Canvas.GetLeft(fe);
                var y = Canvas.GetTop(fe);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                bounds.Union(new Rect(x, y, fe.ActualWidth, fe.ActualHeight));
            }
        }
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            System.Windows.MessageBox.Show("Whiteboard is empty.", "Export");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            DefaultExt = ".png",
            FileName = $"whiteboard_export_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;

        bounds.Inflate(20, 20);
        var rtb = new RenderTargetBitmap((int)WhiteboardCanvas.Width, (int)WhiteboardCanvas.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(WhiteboardCanvas);

        var cropRect = new Int32Rect(
            Math.Max(0, (int)bounds.Left),
            Math.Max(0, (int)bounds.Top),
            (int)Math.Min(WhiteboardCanvas.Width - Math.Max(0, (int)bounds.Left), bounds.Width),
            (int)Math.Min(WhiteboardCanvas.Height - Math.Max(0, (int)bounds.Top), bounds.Height)
        );

        if (cropRect.Width > 0 && cropRect.Height > 0)
        {
            var cb = new CroppedBitmap(rtb, cropRect);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cb));
            using var fs = File.OpenWrite(dlg.FileName);
            encoder.Save(fs);
        }
    }

    private void OnShowManual(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Shortcuts and Controls:\n" +
            " - Ctrl + Scroll : Scale/Zoom the whiteboard.\n" +
            " - Alt + Scroll : Scale/Zoom selected image or shape.\n" +
            " - Drag mouse with 'Pan' toggled : Pan the whiteboard.\n" +
            " - Ctrl + V : Paste image from clipboard.\n" +
            " - Drag & Drop : Drop images directly onto whiteboard.\n\n" +
            "Document Mode:\n" +
            " - 1st in Android activate whiteboard tab then click document mode on pc app\n\n" +
            "Overlay Mode:\n" +
            " - 1st in Android activate overlay tab then click overlay mode on pc app",
            "Manual & Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnBgColorChanged(object sender, TextChangedEventArgs e)
    {
        if (BgColorInput == null || WhiteboardCanvas == null || ScrollHost == null) return;
        try
        {
            if (new BrushConverter().ConvertFromString(BgColorInput.Text) is SolidColorBrush brush)
            {
                WhiteboardCanvas.Background = brush;
                ScrollHost.Background = brush;
                this.Background = brush;
            }
        }
        catch { }
    }

    private void SyncElementPosition(UIElement element)
    {
        if (element is not FrameworkElement fe || fe.Tag is not string id) return;
        var x = Canvas.GetLeft(fe);
        var y = Canvas.GetTop(fe);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;

        if (fe is Image img)
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "wb-image-move",
                id,
                x,
                y,
                w = img.Width,
                h = img.Height
            });
            _ = _networkService.BroadcastJsonAsync(msg);
        }
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Mouse position in canvas-local coordinates (before transform)
            var mouseCanvas = e.GetPosition(WhiteboardCanvas);

            var oldZoom = _zoom;
            var factor = e.Delta > 0 ? 1.1 : 0.9;
            _zoom = Math.Max(0.1, Math.Min(5.0, _zoom * factor));
            CanvasScale.ScaleX = _zoom;
            CanvasScale.ScaleY = _zoom;

            // Keep the canvas point under the cursor fixed on screen.
            // screen = point * zoom + pan  →  newPan = pan + point * (oldZoom - newZoom)
            _panX += mouseCanvas.X * (oldZoom - _zoom);
            _panY += mouseCanvas.Y * (oldZoom - _zoom);
            CanvasTranslate.X = _panX;
            CanvasTranslate.Y = _panY;

            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var pos = e.GetPosition(WhiteboardCanvas);
            foreach (var file in files)
            {
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                {
                    AddImageToCanvas(file, pos.X, pos.Y);
                    pos = new Point(pos.X + 20, pos.Y + 20);
                }
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPanToggle(object sender, RoutedEventArgs e)
    {
        _isPanMode = PanToggle.IsChecked == true;
        ScrollHost.Cursor = _isPanMode ? Cursors.Hand : null;
        // Disable ScrollViewer scrolling in pan mode
        ScrollHost.HorizontalScrollBarVisibility = _isPanMode ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ScrollHost.VerticalScrollBarVisibility = _isPanMode ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnScrollHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanMode || e.ChangedButton != MouseButton.Left) return;
        _panMouseStart = e.GetPosition(ScrollHost);
        _panStartX = _panX;
        _panStartY = _panY;
        ScrollHost.CaptureMouse();
        ScrollHost.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnScrollHostMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanMode || e.LeftButton != MouseButtonState.Pressed || !ScrollHost.IsMouseCaptured) return;
        var pos = e.GetPosition(ScrollHost);
        var dx = pos.X - _panMouseStart.X;
        var dy = pos.Y - _panMouseStart.Y;
        _panX = _panStartX + dx;
        _panY = _panStartY + dy;
        CanvasTranslate.X = _panX;
        CanvasTranslate.Y = _panY;
        e.Handled = true;
    }

    private void OnScrollHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanMode || e.ChangedButton != MouseButton.Left || !ScrollHost.IsMouseCaptured) return;
        ScrollHost.ReleaseMouseCapture();
        ScrollHost.Cursor = Cursors.Hand;
        e.Handled = true;
    }
}
