using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

namespace Inkbridge.Windows;

public partial class WhiteboardWindow : Window
{
    private readonly NetworkService _networkService;
    private double _zoom = 1.0;

    public WhiteboardWindow(NetworkService networkService)
    {
        InitializeComponent();
        _networkService = networkService;
    }

    /// <summary>
    /// Called by NetworkService when a whiteboard message arrives from Android.
    /// Must be invoked on UI thread.
    /// </summary>
    public void HandleWhiteboardMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "wb-stroke")
            {
                var pointsEl = doc.RootElement.GetProperty("points");
                var points = new List<Point>();
                foreach (var pt in pointsEl.EnumerateArray())
                {
                    points.Add(new Point(pt.GetProperty("x").GetDouble(), pt.GetProperty("y").GetDouble()));
                }

                var width = doc.RootElement.TryGetProperty("width", out var wEl) ? wEl.GetDouble() : 4.0;
                var colorVal = doc.RootElement.TryGetProperty("color", out var cEl) ? (ulong)cEl.GetInt64() : 0xFFFFFFFF;

                var a = (byte)((colorVal >> 24) & 0xFF);
                var r = (byte)((colorVal >> 16) & 0xFF);
                var g = (byte)((colorVal >> 8) & 0xFF);
                var b = (byte)(colorVal & 0xFF);

                DrawStroke(points, Color.FromArgb(a, r, g, b), width);
            }
            else if (type == "wb-clear")
            {
                WhiteboardCanvas.Children.Clear();
            }
        }
        catch { }
    }

    private void DrawStroke(List<Point> points, Color color, double width)
    {
        if (points.Count < 2) return;

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        foreach (var pt in points)
            polyline.Points.Add(pt);

        WhiteboardCanvas.Children.Add(polyline);
    }

    private void OnAddImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
        };
        if (dlg.ShowDialog() == true)
        {
            AddImageToCanvas(dlg.FileName, 100, 100);
        }
    }

    private void AddImageToCanvas(string filePath, double x, double y)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                Width = Math.Min(bitmap.PixelWidth, 600),
                Stretch = Stretch.Uniform
            };

            Canvas.SetLeft(image, x);
            Canvas.SetTop(image, y);
            WhiteboardCanvas.Children.Add(image);

            // Make draggable & resizable
            MakeDraggable(image);
            MakeResizable(image);
        }
        catch { }
    }

    private void OnAddLink(object sender, RoutedEventArgs e)
    {
        // Simple input dialog using WPF
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

    private void OnClear(object sender, RoutedEventArgs e)
    {
        WhiteboardCanvas.Children.Clear();
        // Notify Android
        var msg = JsonSerializer.Serialize(new { type = "wb-clear" });
        _ = _networkService.BroadcastJsonAsync(msg);
    }

    // Drag support
    private bool _isDragging;
    private Point _dragStart;
    private double _dragOrigX, _dragOrigY;
    private UIElement? _dragTarget;

    private void MakeDraggable(UIElement element)
    {
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
                _dragTarget = null;
            }
        };
    }

    // Resize support for images
    private void MakeResizable(Image image)
    {
        image.MouseRightButtonDown += (s, e) =>
        {
            // Toggle between original size and 300px width
            if (image.Width > 300)
                image.Width = 300;
            else
                image.Width = 600;
            e.Handled = true;
        };

        // Mouse wheel on image to resize
        image.MouseWheel += (s, e) =>
        {
            var factor = e.Delta > 0 ? 1.1 : 0.9;
            image.Width = Math.Max(50, Math.Min(2000, image.Width * factor));
            e.Handled = true;
        };
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+wheel to zoom the whole canvas
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var factor = e.Delta > 0 ? 1.1 : 0.9;
            _zoom = Math.Max(0.1, Math.Min(5.0, _zoom * factor));
            CanvasScale.ScaleX = _zoom;
            CanvasScale.ScaleY = _zoom;
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
                    pos = new Point(pos.X + 20, pos.Y + 20); // offset stacked images
                }
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
