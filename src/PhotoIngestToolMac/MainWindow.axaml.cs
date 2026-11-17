using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Layout;
using PhotoIngestTool.Models;
using PhotoIngestTool.Services;
using PhotoIngestTool.ViewModels;

namespace PhotoIngestTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UpdateService _updateService = new();
    private bool _isUpdateModalOpen;
    private Avalonia.Threading.DispatcherTimer? _periodicUpdateTimer;

    public MainWindow() : this(null) { }

    public MainWindow(string? startupSourcePath = null)
    {
        InitializeComponent();

        var metadataService = new MetadataService();
        var driveWatcherService = new DriveWatcherService();
        var copyEngineService = new CopyEngineService(metadataService, driveWatcherService);

        _viewModel = new MainViewModel(driveWatcherService, metadataService, copyEngineService);
        DataContext = _viewModel;

        _viewModel.JobFinishedNotification += OnJobFinished;
        _viewModel.CardInsertedAttentionRequested += OnCardInsertedAttentionRequested;

        _viewModel.FilterViewModel.AlertRequested += async (title, msg) =>
        {
            var warn = new Window
            {
                Title = title,
                Width = 420,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#0F1117")),
                Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
            };
            var wStack = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            wStack.Children.Add(new TextBlock
            {
                Text = msg,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
            });
            var wBtn = new Button
            {
                Content = "OK",
                Classes = { "primaryBtn" },
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(24, 6, 24, 6)
            };
            wBtn.Click += (_, _) => warn.Close();
            wStack.Children.Add(wBtn);
            warn.Content = wStack;
            await warn.ShowDialog(this);
        };

        _viewModel.FilterViewModel.ClipboardCopyRequested += async (text) =>
        {
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(text);
            }
        };

        _viewModel.FilterViewModel.ConfirmRequested += async (title, msg) =>
        {
            bool result = false;
            var dlg = new Window
            {
                Title = title,
                Width = 440,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#0F1117")),
                Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
            };
            var sp = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            sp.Children.Add(new TextBlock
            {
                Text = msg,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#FBBF24"))
            });
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
            var btnYes = new Button { Content = "Tiếp Tục", Classes = { "primaryBtn" }, Padding = new Thickness(16, 6, 16, 6) };
            var btnNo = new Button { Content = "Hủy", Classes = { "actionBtn" }, Padding = new Thickness(16, 6, 16, 6) };
            btnYes.Click += (_, _) => { result = true; dlg.Close(); };
            btnNo.Click += (_, _) => { result = false; dlg.Close(); };
            bp.Children.Add(btnNo);
            bp.Children.Add(btnYes);
            sp.Children.Add(bp);
            dlg.Content = sp;
            await dlg.ShowDialog(this);
            return result;
        };

        driveWatcherService.StartWatching();

        if (!string.IsNullOrWhiteSpace(startupSourcePath))
        {
            _ = _viewModel.HandleStartupSourcePathAsync(startupSourcePath);
        }

        // 1. Tự động kiểm tra bản cập nhật mới ngầm sau khi mở ứng dụng trên macOS (sau 2.5 giây)
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2500);
            await CheckForUpdateInternalAsync(isManualCheck: false);
        });

        // 2. Định kỳ kiểm tra cập nhật mỗi 45 phút khi ứng dụng đang chạy
        _periodicUpdateTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(45)
        };
        _periodicUpdateTimer.Tick += async (_, _) =>
        {
            await CheckForUpdateInternalAsync(isManualCheck: false);
        };
        _periodicUpdateTimer.Start();
    }

    private bool _isExplicitExit;

    public void ForceExit()
    {
        _isExplicitExit = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void OnCardInsertedAttentionRequested(object? sender, EventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private async void OnBrowseSourceClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Chọn ổ đĩa thẻ nhớ (/Volumes/...) hoặc thư mục chứa ảnh/video",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? result = folders[0].Path.IsAbsoluteUri ? System.Uri.UnescapeDataString(folders[0].Path.AbsolutePath) : null;
            if (!string.IsNullOrWhiteSpace(result))
            {
                _viewModel.SourcePath = result;
                await _viewModel.ScanSourceFolderAsync();
            }
        }
    }

    private async void OnBrowsePrimaryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Chọn thư mục gốc ổ cứng lưu trữ chính (RootFolder)",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? result = folders[0].Path.IsAbsoluteUri ? System.Uri.UnescapeDataString(folders[0].Path.AbsolutePath) : null;
            if (!string.IsNullOrWhiteSpace(result))
            {
                _viewModel.PrimaryRootFolder = result;
                _viewModel.RefreshDriveSpaceTelemetry();
            }
        }
    }

    private async void OnBrowseBackupClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Chọn thư mục gốc ổ cứng lưu trữ Backup (Dual Backup)",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? result = folders[0].Path.IsAbsoluteUri ? System.Uri.UnescapeDataString(folders[0].Path.AbsolutePath) : null;
            if (!string.IsNullOrWhiteSpace(result))
            {
                _viewModel.BackupRootFolder = result;
                _viewModel.RefreshDriveSpaceTelemetry();
            }
        }
    }

    private async void OnBrowseFilterSourceClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Chọn thư mục ảnh nguồn cần lọc (chứa file ảnh, RAW...)",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? result = folders[0].Path.IsAbsoluteUri ? System.Uri.UnescapeDataString(folders[0].Path.AbsolutePath) : null;
            if (!string.IsNullOrWhiteSpace(result))
            {
                _viewModel.FilterViewModel.SourceDirectory = result;
            }
        }
    }

    private async void OnBrowseFilterDestinationClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Chọn thư mục lưu các ảnh sau khi lọc",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? result = folders[0].Path.IsAbsoluteUri ? System.Uri.UnescapeDataString(folders[0].Path.AbsolutePath) : null;
            if (!string.IsNullOrWhiteSpace(result))
            {
                _viewModel.FilterViewModel.DestinationDirectory = result;
            }
        }
    }

    private void OnCardDatesBannerClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        ShowDateFilterModal();
    }

    private void OnOpenDateFilterClick(object? sender, RoutedEventArgs e)
    {
        ShowDateFilterModal();
    }

    private void ShowDateFilterModal()
    {
        if (_viewModel.ShootDateGroups.Count == 0)
        {
            _viewModel.AppendLog("[Thông báo] Chưa phát hiện ngày chụp nào trên thẻ. Vui lòng cắm thẻ nhớ hoặc bấm nút [Quét] trước.");
            return;
        }

        var dialog = new Window
        {
            Title = "Bảng Chọn Ngày Chụp Cần Copy - Chép Thẻ Nhanh Pro (macOS)",
            Width = 660,
            Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D97706")),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8)
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        int totalFilesAll = _viewModel.ShootDateGroups.Sum(x => x.FileCount);
        long totalBytesAll = _viewModel.ShootDateGroups.Sum(x => x.TotalBytes);

        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = $"📅 BẢNG CHỌN NGÀY CHỤP CẦN COPY ({_viewModel.ShootDateGroups.Count} ngày trên thẻ)",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FBBF24"))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = $"Tổng toàn thẻ: {totalFilesAll:N0} file ({CopyEngineService.FormatBytes(totalBytesAll)}). Hãy tick chọn các ngày bạn muốn chép:",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
        });
        Grid.SetRow(headerStack, 0);
        mainGrid.Children.Add(headerStack);

        var tempSelections = _viewModel.ShootDateGroups.ToDictionary(g => g.Date, g => g.IsSelected);

        var toolPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var btnLatestOnly = new Button
        {
            Content = "🌟 Chỉ chọn ngày mới nhất",
            Classes = { "actionBtn" },
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            FontWeight = FontWeight.SemiBold
        };

        var btnSelectAll = new Button
        {
            Content = "✓ Chọn tất cả",
            Classes = { "actionBtn" },
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        };

        var btnDeselectAll = new Button
        {
            Content = "✗ Bỏ chọn tất cả",
            Classes = { "actionBtn" },
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = new SolidColorBrush(Color.Parse("#F87171"))
        };

        toolPanel.Children.Add(btnLatestOnly);
        toolPanel.Children.Add(btnSelectAll);
        toolPanel.Children.Add(btnDeselectAll);
        Grid.SetRow(toolPanel, 1);
        mainGrid.Children.Add(toolPanel);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var itemsStack = new StackPanel { Spacing = 6 };
        scrollViewer.Content = itemsStack;
        Grid.SetRow(scrollViewer, 2);
        mainGrid.Children.Add(scrollViewer);

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var summaryText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        };
        Grid.SetColumn(summaryText, 0);
        footerGrid.Children.Add(summaryText);

        var actionBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        var btnCancel = new Button
        {
            Content = "Hủy bỏ",
            Classes = { "actionBtn" },
            Padding = new Thickness(14, 6, 14, 6)
        };
        btnCancel.Click += (_, _) => dialog.Close();

        var btnApply = new Button
        {
            Content = "Áp Dụng Lọc",
            Classes = { "primaryBtn" },
            Padding = new Thickness(18, 6, 18, 6)
        };
        btnApply.Click += (_, _) =>
        {
            var selectedDates = tempSelections.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            if (selectedDates.Count == 0)
            {
                _viewModel.AppendLog("[Cảnh báo] Vui lòng tick chọn ít nhất 1 ngày chụp để copy.");
                return;
            }

            _viewModel.ApplyDateFilter(selectedDates);
            dialog.Close();
        };

        actionBtns.Children.Add(btnCancel);
        actionBtns.Children.Add(btnApply);
        Grid.SetColumn(actionBtns, 1);
        footerGrid.Children.Add(actionBtns);
        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        void UpdateSummaryUI()
        {
            int selDays = tempSelections.Values.Count(v => v);
            var selectedItems = _viewModel.ShootDateGroups.Where(g => tempSelections[g.Date]).ToList();
            int selFiles = selectedItems.Sum(g => g.FileCount);
            long selBytes = selectedItems.Sum(g => g.TotalBytes);
            summaryText.Text = $"Đã chọn: {selDays}/{_viewModel.ShootDateGroups.Count} ngày  •  {selFiles:N0} file ({CopyEngineService.FormatBytes(selBytes)})";
            btnApply.Content = $"Áp Dụng Lọc ({selFiles:N0} file)";
        }

        void RenderDateList()
        {
            itemsStack.Children.Clear();
            bool isFirst = true;
            foreach (var item in _viewModel.ShootDateGroups)
            {
                bool isNewest = isFirst;
                isFirst = false;

                bool isSelected = tempSelections[item.Date];

                var card = new Border
                {
                    Background = new SolidColorBrush(isSelected ? Color.Parse("#181C26") : Color.Parse("#11141C")),
                    BorderBrush = new SolidColorBrush(isSelected ? Color.Parse("#FBBF24") : Color.Parse("#252E42")),
                    BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };

                var cardGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("32,*")
                };

                var chk = new CheckBox
                {
                    IsChecked = isSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(chk, 0);
                cardGrid.Children.Add(chk);

                var infoStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 3
                };

                var line1 = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };
                line1.Children.Add(new TextBlock
                {
                    Text = $"📅 {item.DateFormatted}",
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(isSelected ? Color.Parse("#FBBF24") : Color.Parse("#F1F5F9"))
                });

                if (isNewest)
                {
                    line1.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#0C4A6E")),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "MỚI NHẤT", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#38BDF8")) }
                    });
                }

                if (isSelected)
                {
                    line1.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#064E3B")),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "SẼ COPY", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#34D399")) }
                    });
                }

                infoStack.Children.Add(line1);

                infoStack.Children.Add(new TextBlock
                {
                    Text = $"📦 {item.FileCount:N0} file ({item.TotalSizeText})   |   RAW: {item.RawCount}  •  JPG: {item.JpgCount}  •  Video: {item.VideoCount}",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
                });

                Grid.SetColumn(infoStack, 1);
                cardGrid.Children.Add(infoStack);
                card.Child = cardGrid;

                void Toggle()
                {
                    tempSelections[item.Date] = !tempSelections[item.Date];
                    RenderDateList();
                    UpdateSummaryUI();
                }

                card.PointerPressed += (_, _) => Toggle();

                itemsStack.Children.Add(card);
            }
        }

        btnLatestOnly.Click += (_, _) =>
        {
            var first = _viewModel.ShootDateGroups.FirstOrDefault();
            foreach (var k in tempSelections.Keys.ToList())
            {
                tempSelections[k] = (first != null && k == first.Date);
            }
            RenderDateList();
            UpdateSummaryUI();
        };

        btnSelectAll.Click += (_, _) =>
        {
            foreach (var k in tempSelections.Keys.ToList())
            {
                tempSelections[k] = true;
            }
            RenderDateList();
            UpdateSummaryUI();
        };

        btnDeselectAll.Click += (_, _) =>
        {
            foreach (var k in tempSelections.Keys.ToList())
            {
                tempSelections[k] = false;
            }
            RenderDateList();
            UpdateSummaryUI();
        };

        RenderDateList();
        UpdateSummaryUI();

        rootBorder.Child = mainGrid;
        dialog.Content = rootBorder;
        dialog.ShowDialog(this);
    }

    private void OnOpenHistoryClick(object? sender, RoutedEventArgs e)
    {
        ShowHistoryModal();
    }

    private void ShowHistoryModal()
    {
        var dialog = new Window
        {
            Title = "Lịch Sử Các Job Đã Đổ Thẻ - Chép Thẻ Nhanh Pro (macOS)",
            Width = 710,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8)
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var headerText = new TextBlock
        {
            Text = $"📜 LỊCH SỬ ĐỔ THẺ ({_viewModel.IngestHistory.Count} Job đã lưu)",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(headerText, 0);
        mainGrid.Children.Add(headerText);

        // Search Bar
        var searchGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("95,*"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        searchGrid.Children.Add(new TextBlock
        {
            Text = "🔍 Tìm kiếm:",
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
            VerticalAlignment = VerticalAlignment.Center
        });

        var txtSearch = new TextBox
        {
            Watermark = "Nhập tên khách hàng, loại job, ngày chụp hoặc tên thợ..."
        };
        Grid.SetColumn(txtSearch, 1);
        searchGrid.Children.Add(txtSearch);
        Grid.SetRow(searchGrid, 1);
        mainGrid.Children.Add(searchGrid);

        // Scrollable List
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var itemsStack = new StackPanel();
        scrollViewer.Content = itemsStack;
        Grid.SetRow(scrollViewer, 2);
        mainGrid.Children.Add(scrollViewer);

        void RenderList(string keyword)
        {
            itemsStack.Children.Clear();
            var filtered = _viewModel.IngestHistory.Where(x =>
                string.IsNullOrWhiteSpace(keyword) ||
                x.ClientName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.JobType.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.ShootDateText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.PhotographerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.PrimaryDirectory.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                itemsStack.Children.Add(new TextBlock
                {
                    Text = _viewModel.IngestHistory.Count == 0
                        ? "Chưa có lịch sử đổ thẻ nào. Sau mỗi lần bạn đổ thẻ thành công, thông tin khách hàng và đường dẫn thư mục sẽ được tự động lưu tại đây."
                        : "Không tìm thấy Job nào khớp với từ khóa tìm kiếm.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(4, 16, 4, 16)
                });
                return;
            }

            foreach (var item in filtered)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#11141C")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#252E42")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

                var infoStack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"👤 {item.ClientName}  •  {item.JobType}   (Ngày chụp: {item.ShootDateText})",
                    FontSize = 12.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                });

                string splitBadge = item.SplitByFileType ? "Tách RAW/JPG/Video" : "Chung thư mục";
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"🕒 {item.CompletedAt:dd/MM/yyyy HH:mm}  |  📦 {item.VerifiedFiles}/{item.TotalFiles} file ({item.TotalSizeText})  |  📷 {item.PhotographerName} ({item.CameraModel})  |  {splitBadge}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#34D399")),
                    Margin = new Thickness(0, 3, 0, 3)
                });

                infoStack.Children.Add(new TextBlock
                {
                    Text = $"📁 {item.PrimaryDirectory}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [ToolTip.TipProperty] = item.PrimaryDirectory
                });

                Grid.SetColumn(infoStack, 0);
                cardGrid.Children.Add(infoStack);

                var actionStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 6
                };

                var btnOpenDir = new Button
                {
                    Content = "📂 Mở Thư Mục",
                    Classes = { "actionBtn" },
                    Padding = new Thickness(10, 5, 10, 5),
                    Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                };
                btnOpenDir.Click += (_, _) =>
                {
                    if (Directory.Exists(item.PrimaryDirectory))
                    {
                        PlatformHelper.OpenFolder(item.PrimaryDirectory);
                    }
                    else
                    {
                        _viewModel.AppendLog($"[Cảnh báo] Thư mục không còn tồn tại: {item.PrimaryDirectory}");
                    }
                };

                var btnOpenLog = new Button
                {
                    Content = "📄 Log",
                    Classes = { "actionBtn" },
                    Padding = new Thickness(8, 5, 8, 5)
                };
                btnOpenLog.Click += (_, _) =>
                {
                    if (!string.IsNullOrWhiteSpace(item.ReportFilePath) && File.Exists(item.ReportFilePath))
                    {
                        PlatformHelper.OpenTextFile(item.ReportFilePath);
                    }
                    else
                    {
                        _viewModel.AppendLog("[Thông báo] Không tìm thấy file báo cáo ingest_report.txt của Job này.");
                    }
                };

                actionStack.Children.Add(btnOpenDir);
                actionStack.Children.Add(btnOpenLog);
                Grid.SetColumn(actionStack, 1);
                cardGrid.Children.Add(actionStack);

                card.Child = cardGrid;
                itemsStack.Children.Add(card);
            }
        }

        txtSearch.TextChanged += (_, _) => RenderList((txtSearch.Text ?? "").Trim());
        RenderList(string.Empty);

        // Footer
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        var btnClear = new Button
        {
            Content = "🗑 Xóa Lịch Sử",
            Classes = { "actionBtn" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
            Foreground = new SolidColorBrush(Color.Parse("#F87171"))
        };
        btnClear.Click += (_, _) =>
        {
            if (_viewModel.IngestHistory.Count == 0) return;
            _viewModel.ClearIngestHistory();
            headerText.Text = "📜 LỊCH SỬ ĐỔ THẺ (0 Job đã lưu)";
            RenderList(string.Empty);
        };

        var btnClose = new Button
        {
            Content = "Đóng (OK)",
            Classes = { "primaryBtn" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(22, 6, 22, 6)
        };
        btnClose.Click += (_, _) => dialog.Close();

        Grid.SetColumn(btnClose, 1);
        footerGrid.Children.Add(btnClear);
        footerGrid.Children.Add(btnClose);
        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        rootBorder.Child = mainGrid;
        dialog.Content = rootBorder;
        dialog.ShowDialog(this);
    }

    private void OnFilterHistoryClick(object? sender, RoutedEventArgs e)
    {
        ShowFilterHistoryModal();
    }

    private void ShowFilterHistoryModal()
    {
        var filterVm = _viewModel.FilterViewModel;
        var dialog = new Window
        {
            Title = "Lịch Sử Lọc Ảnh Theo Mã - Chép Thẻ Nhanh Pro (macOS)",
            Width = 730,
            Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8)
        };

        var mainGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };

        // 1. Header
        var headerText = new TextBlock
        {
            Text = $"📜 LỊCH SỬ LỌC ẢNH ({filterVm.FilterHistory.Count} Lần lọc đã lưu)",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(headerText, 0);
        mainGrid.Children.Add(headerText);

        // 2. Search Bar
        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("95,*"), Margin = new Thickness(0, 0, 0, 10) };
        searchGrid.Children.Add(new TextBlock
        {
            Text = "🔍 Tìm kiếm:",
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
            VerticalAlignment = VerticalAlignment.Center
        });

        var txtSearch = new TextBox();
        Grid.SetColumn(txtSearch, 1);
        searchGrid.Children.Add(txtSearch);
        Grid.SetRow(searchGrid, 1);
        mainGrid.Children.Add(searchGrid);

        // 3. Scrollable List of History Cards
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var itemsStack = new StackPanel { Spacing = 8 };
        scrollViewer.Content = itemsStack;
        Grid.SetRow(scrollViewer, 2);
        mainGrid.Children.Add(scrollViewer);

        void RenderList(string keyword)
        {
            itemsStack.Children.Clear();
            var filtered = filterVm.FilterHistory.Where(x =>
                string.IsNullOrWhiteSpace(keyword) ||
                (x.SourceDirectory != null && x.SourceDirectory.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (x.EffectiveDestinationFolder != null && x.EffectiveDestinationFolder.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (x.FileTypes != null && x.FileTypes.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (x.RawSequenceText != null && x.RawSequenceText.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (x.MissingCodes != null && x.MissingCodes.Any(c => c.Contains(keyword, StringComparison.OrdinalIgnoreCase))) ||
                x.FilteredAt.ToString("dd/MM/yyyy HH:mm").Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                itemsStack.Children.Add(new TextBlock
                {
                    Text = filterVm.FilterHistory.Count == 0
                        ? "Chưa có lịch sử lọc ảnh nào. Sau mỗi lần lọc thành công, thông tin sẽ được tự động lưu tại đây."
                        : "Không tìm thấy lần lọc ảnh nào khớp với từ khóa tìm kiếm.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(4, 16, 4, 16)
                });
                return;
            }

            foreach (var item in filtered)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#11141C")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#252E42")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 9, 12, 9)
                };

                var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var infoStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 10, 0) };

                infoStack.Children.Add(new TextBlock
                {
                    Text = $"🕒 Lọc lúc: {item.FilteredAt:dd/MM/yyyy HH:mm}   •   Đã lọc: {item.FilteredFilesCount} ảnh ({item.ElapsedSeconds:F1}s)   •   Định dạng: {item.FileTypes}",
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                });

                if (item.MissingCodesCount > 0)
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = $"⚠️ THIẾU {item.MissingCodesCount} MÃ: {string.Join(", ", item.MissingCodes)}",
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#F87171"))
                    });
                }
                else if (item.RequestedCodesCount > 0)
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = $"✅ Đã tìm thấy đủ 100% ({item.FoundCodesCount}/{item.RequestedCodesCount} mã yêu cầu)",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#34D399"))
                    });
                }

                infoStack.Children.Add(new TextBlock
                {
                    Text = $"📁 Nguồn: {item.SourceDirectory}",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                });
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"📂 Đích : {item.EffectiveDestinationFolder}",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                });

                Grid.SetColumn(infoStack, 0);
                cardGrid.Children.Add(infoStack);

                var actionStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 6
                };

                if (item.MissingCodesCount > 0)
                {
                    var btnCopyMissing = new Button
                    {
                        Content = "📋 Copy Mã Thiếu",
                        Classes = { "actionBtn" },
                        Padding = new Thickness(8, 5, 8, 5),
                        Foreground = new SolidColorBrush(Color.Parse("#F87171"))
                    };
                    btnCopyMissing.Click += async (_, _) =>
                    {
                        try
                        {
                            string textToCopy = $"Mã ảnh không tìm thấy ({item.MissingCodesCount} mã): {string.Join(", ", item.MissingCodes)}";
                            if (Clipboard != null) await Clipboard.SetTextAsync(textToCopy);
                        }
                        catch { }
                    };
                    actionStack.Children.Add(btnCopyMissing);
                }

                var btnOpenDir = new Button
                {
                    Content = "📂 Mở Thư Mục",
                    Classes = { "actionBtn" },
                    Padding = new Thickness(10, 5, 10, 5),
                    Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                };
                btnOpenDir.Click += (_, _) =>
                {
                    string target = !string.IsNullOrWhiteSpace(item.EffectiveDestinationFolder) && Directory.Exists(item.EffectiveDestinationFolder)
                        ? item.EffectiveDestinationFolder
                        : item.DestinationDirectory;

                    if (Directory.Exists(target))
                    {
                        PlatformHelper.OpenFolder(target);
                    }
                };
                actionStack.Children.Add(btnOpenDir);

                Grid.SetColumn(actionStack, 1);
                cardGrid.Children.Add(actionStack);

                card.Child = cardGrid;
                itemsStack.Children.Add(card);
            }
        }

        txtSearch.TextChanged += (_, _) => RenderList((txtSearch.Text ?? "").Trim());
        RenderList(string.Empty);

        // 4. Footer
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var btnClear = new Button
        {
            Content = "🗑️ Xóa Lịch Sử Lọc",
            Classes = { "actionBtn" },
            Padding = new Thickness(12, 6, 12, 6),
            Foreground = new SolidColorBrush(Color.Parse("#F87171"))
        };
        btnClear.Click += (_, _) =>
        {
            if (filterVm.FilterHistory.Count == 0) return;
            filterVm.ClearFilterHistory();
            headerText.Text = "📜 LỊCH SỬ LỌC ẢNH (0 Lần lọc đã lưu)";
            RenderList(string.Empty);
        };

        var btnClose = new Button
        {
            Content = "Đóng (OK)",
            Classes = { "primaryBtn" },
            Padding = new Thickness(22, 6, 22, 6)
        };
        btnClose.Click += (_, _) => dialog.Close();

        Grid.SetColumn(btnClose, 1);
        footerGrid.Children.Add(btnClear);
        footerGrid.Children.Add(btnClose);
        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        rootBorder.Child = mainGrid;
        dialog.Content = rootBorder;
        dialog.ShowDialog(this);
    }

    private async void OnAboutInfoClick(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        string? originalContent = btn?.Content?.ToString();

        try
        {
            if (btn != null)
            {
                btn.IsEnabled = false;
                btn.Content = "⏳ Đang kiểm tra...";
            }

            var info = await _updateService.CheckForUpdateAsync();
            if (info != null && info.IsUpdateAvailable)
            {
                await ShowUpdatePromptModal(info);
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Auto check update in About click failed: {ex.Message}");
        }
        finally
        {
            if (btn != null)
            {
                btn.IsEnabled = true;
                btn.Content = originalContent ?? "ⓘ Thông tin";
            }
        }

        await ShowAboutInfoModalAsync();
    }

    private async Task ShowAboutInfoModalAsync()
    {
        const string facebookUrl = "https://www.facebook.com/HoangdiepLN";
        const string githubUrl = "https://github.com/hoangdiep99/ChepTheNhanhPro";

        var dialog = new Window
        {
            Title = "Thông Tin Tác Giả & Hướng Dẫn Sử Dụng - Chép Thẻ Nhanh Pro (macOS)",
            Width = 690,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8)
        };

        var mainGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        // Header
        var titleStack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        titleStack.Children.Add(new TextBlock
        {
            Text = "CHÉP THẺ NHANH PRO - QUẢN LÝ FILE ẢNH CHUYÊN NGHIỆP",
            FontSize = 14.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F8FAFC"))
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"macOS Edition • v{UpdateService.CurrentVersion} Pro (arm64 / x64 • .NET 8 Avalonia Studio Edition)",
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetRow(titleStack, 0);
        mainGrid.Children.Add(titleStack);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var contentStack = new StackPanel { Spacing = 10 };

        // Card A: Author & Contact
        var authorBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#11141C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#252E42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9)
        };
        var authorStack = new StackPanel { Spacing = 4 };
        authorStack.Children.Add(new TextBlock
        {
            Text = "1. THÔNG TIN TÁC GIẢ & LIÊN HỆ",
            FontSize = 11.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        });
        authorStack.Children.Add(new TextBlock
        {
            Text = "👤 Tác giả phát triển :  Hoàng Điệp",
            FontSize = 12.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F1F5F9"))
        });

        var fbText = new TextBlock
        {
            Text = "🌐 Facebook liên hệ  :  " + facebookUrl + "  (Nhấn để mở)",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        fbText.PointerPressed += (_, _) => PlatformHelper.OpenUrl(facebookUrl);
        authorStack.Children.Add(fbText);

        var ghText = new TextBlock
        {
            Text = "🐙 GitHub dự án       :  " + githubUrl + "  (Nhấn để mở)",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#A78BFA")),
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        ghText.PointerPressed += (_, _) => PlatformHelper.OpenUrl(githubUrl);
        authorStack.Children.Add(ghText);
        authorBorder.Child = authorStack;
        contentStack.Children.Add(authorBorder);

        // Card B: App Intro
        var introBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#11141C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#252E42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9)
        };
        var introStack = new StackPanel { Spacing = 4 };
        introStack.Children.Add(new TextBlock
        {
            Text = "2. GIỚI THIỆU VỀ ỨNG DỤNG (macOS Edition)",
            FontSize = 11.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FBBF24"))
        });
        introStack.Children.Add(new TextBlock
        {
            Text =
                "Chép Thẻ Nhanh Pro là bộ công cụ All-in-One chuyên nghiệp dành cho Nhiếp ảnh gia, Videographer và Studio ảnh tại Việt Nam, tối ưu toàn diện quy trình làm việc với 2 module cốt lõi:\n" +
                "• ⚡ ĐỔ THẺ & SAO CHÉP TỐC ĐỘ CAO: Tự động theo dõi /Volumes trên macOS khi cắm thẻ SD / CFexpress / XQD, tự bật app; sao chép luồng kép (Dual-Backup) vào 2 ổ cứng; tự tách sẵn thư mục con RAW / JPG / Video; kiểm tra mã băm xxHash64 100% từng file chống lỗi; tự động ngắt thẻ an toàn (diskutil eject) và lưu lịch sử đổ thẻ 1-click tra cứu.\n" +
                "• 🎯 LỌC ẢNH THEO MÃ SIÊU TỐC: Tự động bóc tách và lọc hàng loạt file ảnh theo danh sách mã khách gửi từ tin nhắn Zalo, Facebook Messenger hoặc file text; hỗ trợ tìm theo số đuôi (3 số, 4 số...), mã máy ảnh (DSC, IMG, _MG...); tích hợp công cụ Đối Soát & Báo Cáo Mã Thiếu (Missing Inspector) kèm nút 1-click copy danh sách gửi ngay lại cho khách đối soát.",
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
        });
        introBorder.Child = introStack;
        contentStack.Children.Add(introBorder);

        // Card C: User Guide
        var guideBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#11141C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#25324E")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var guideStack = new StackPanel { Spacing = 4 };
        guideStack.Children.Add(new TextBlock
        {
            Text = "3. HƯỚNG DẪN SỬ DỤNG CHI TIẾT (NGẮN GỌN & DỄ HIỂU)",
            FontSize = 11.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
        });
        guideStack.Children.Add(new TextBlock
        {
            Text =
                "⚡ PHẦN 1: TAB ĐỔ THẺ & SAO CHÉP DỮ LIỆU\n" +
                "   • Bước 1: Cắm thẻ nhớ vào đầu đọc thẻ kết nối với Mac. Phần mềm tự động nhận ra thẻ qua /Volumes, thống kê số lượng RAW/JPG/Video và cảnh báo nếu còn ảnh cũ nhiều ngày.\n" +
                "   • Bước 2: Chọn thư mục ổ cứng lưu ảnh (bật Dual-Backup nếu muốn chép cùng lúc sang ổ thứ 2 dự phòng). Nhập tên Khách & chọn Loại Job (app tự tạo thư mục chuẩn: Ngày-Tháng-Năm - Tên khách - Loại job).\n" +
                "   • Bước 3: Mặc định app tích sẵn tự chia vào thư mục con RAW / JPG / Video (rất tiện lấy riêng JPG gửi khách chọn ảnh). Bấm [Lưu Thợ] để lần sau cắm thẻ máy đó vào là app tự điền đúng tên thợ chụp.\n" +
                "   • Bước 4: Bấm [▶ BẮT ĐẦU CHÉP]. App sao chép và kiểm tra mã băm xxHash64 100%. Khi hoàn tất, app phát âm thanh Glass.aiff của macOS và tự động ngắt thẻ an toàn qua diskutil eject.\n" +
                "   • Tra cứu lịch sử: Bấm [📜 Lịch Sử Đổ Thẻ] để tìm lại các khách hàng đã chép hoặc mở nhanh thư mục ảnh bằng Finder chỉ với 1 cú click.\n\n" +
                "🎯 PHẦN 2: TAB LỌC ẢNH THEO MÃ (DANH SÁCH KHÁCH CHỌN)\n" +
                "   • Bước 1: Chọn Thư mục nguồn (chứa toàn bộ ảnh chụp) và Thư mục đích (nơi lưu các ảnh được chọn). Đường dẫn được tự động đồng bộ sẵn từ Tab Đổ Thẻ sang cực kỳ tiện lợi.\n" +
                "   • Bước 2: Copy nguyên văn tin nhắn khách gửi (từ Zalo, Facebook, Sheet...) và dán vào ô [Dán danh sách mã ảnh]. Thuật toán thông minh tự động lọc đúng mã ảnh (hỗ trợ cả dấu phẩy, khoảng trắng, xuống dòng hay chỉ gõ số đuôi 0123, 4567...).\n" +
                "   • Bước 3: Chọn định dạng file cần lọc (Tất Cả, Chỉ File RAW, Chỉ JPG...) và chọn thao tác Sao Chép (Copy) hoặc Di Chuyển (Move).\n" +
                "   • Bước 4: Bấm [▶ BẮT ĐẦU LỌC ẢNH]. Các ảnh được chọn sẽ tự động gom vào thư mục con chuẩn ngày: Auto loc anh dd-MM-yyyy.\n" +
                "   • Đối Soát Mã Thiếu (Missing Inspector): Nếu khách gõ sai mã hoặc ảnh không tồn tại, app sẽ hiển thị danh sách mã thiếu trực quan. Chỉ cần bấm nút [📋 Copy Mã Thiếu] là có ngay danh sách vào Clipboard để gửi lại cho khách kiểm tra đối soát cực kỳ tiện lợi!",
            FontSize = 11.5,
            LineHeight = 18.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"))
        });
        guideBorder.Child = guideStack;
        contentStack.Children.Add(guideBorder);

        scrollViewer.Content = contentStack;
        Grid.SetRow(scrollViewer, 1);
        mainGrid.Children.Add(scrollViewer);

        // Footer Buttons (Vivid Solid Colors with Bold White Text)
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var btnCheckUpdate = new Button
        {
            Content = "🔍 Kiểm Tra Cập Nhật",
            Background = new SolidColorBrush(Color.Parse("#059669")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#34D399")),
            BorderThickness = new Thickness(1.2),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(5),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        btnCheckUpdate.Click += async (_, _) =>
        {
            btnCheckUpdate.IsEnabled = false;
            btnCheckUpdate.Content = "⏳ Đang kiểm tra...";
            try
            {
                await CheckForUpdateInternalAsync(isManualCheck: true);
            }
            finally
            {
                btnCheckUpdate.IsEnabled = true;
                btnCheckUpdate.Content = "🔍 Kiểm Tra Cập Nhật";
            }
        };

        var btnOpenGh = new Button
        {
            Content = "🐙 GitHub Dự Án",
            Background = new SolidColorBrush(Color.Parse("#6D28D9")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#A78BFA")),
            BorderThickness = new Thickness(1.2),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(5),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        ToolTip.SetTip(btnOpenGh, "Mở kho lưu trữ GitHub của Chép Thẻ Nhanh Pro");
        btnOpenGh.Click += (_, _) => PlatformHelper.OpenUrl(githubUrl);

        var btnOpenFb = new Button
        {
            Content = "Mở Facebook Hoàng Điệp",
            Background = new SolidColorBrush(Color.Parse("#1877F2")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.2),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(5),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        btnOpenFb.Click += (_, _) => PlatformHelper.OpenUrl(facebookUrl);

        var btnClose = new Button
        {
            Content = "Đã Hiểu / Đóng (OK)",
            Background = new SolidColorBrush(Color.Parse("#0284C7")),
            Foreground = Avalonia.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.2),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(22, 7, 22, 7),
            CornerRadius = new CornerRadius(5),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        btnClose.Click += (_, _) => dialog.Close();

        btnPanel.Children.Add(btnCheckUpdate);
        btnPanel.Children.Add(btnOpenGh);
        btnPanel.Children.Add(btnOpenFb);
        btnPanel.Children.Add(btnClose);
        Grid.SetRow(btnPanel, 2);
        mainGrid.Children.Add(btnPanel);

        rootBorder.Child = mainGrid;
        dialog.Content = rootBorder;
        await dialog.ShowDialog(this);
    }

    private async Task CheckForUpdateInternalAsync(bool isManualCheck)
    {
        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (info != null && info.IsUpdateAvailable)
                {
                    if (_isUpdateModalOpen) return;
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    await ShowUpdatePromptModal(info);
                }
                else if (isManualCheck)
                {
                    var dlg = new Window
                    {
                        Title = "Kiểm Tra Cập Nhật",
                        Width = 420,
                        Height = 150,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Background = new SolidColorBrush(Color.Parse("#0F1117")),
                        Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
                    };
                    var sp = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"🎉 Bạn đang sử dụng phiên bản mới nhất: v{UpdateService.CurrentVersion}!\nChưa có bản cập nhật mới hơn trên hệ thống.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#34D399"))
                    });
                    var okBtn = new Button
                    {
                        Content = "OK",
                        Classes = { "primaryBtn" },
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Padding = new Thickness(24, 6, 24, 6)
                    };
                    okBtn.Click += (_, _) => dlg.Close();
                    sp.Children.Add(okBtn);
                    dlg.Content = sp;
                    await dlg.ShowDialog(this);
                }
            });
        }
        catch (Exception ex)
        {
            if (isManualCheck)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var dlg = new Window
                    {
                        Title = "Lỗi Kiểm Tra Cập Nhật",
                        Width = 420,
                        Height = 150,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Background = new SolidColorBrush(Color.Parse("#0F1117")),
                        Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
                    };
                    var sp = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"⚠ Không thể kết nối đến máy chủ GitHub:\n{ex.Message}\n\nVui lòng thử lại sau!",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.Parse("#F87171"))
                    });
                    var okBtn = new Button
                    {
                        Content = "Đóng",
                        Classes = { "primaryBtn" },
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Padding = new Thickness(24, 6, 24, 6)
                    };
                    okBtn.Click += (_, _) => dlg.Close();
                    sp.Children.Add(okBtn);
                    dlg.Content = sp;
                    await dlg.ShowDialog(this);
                });
            }
        }
    }

    private async Task ShowUpdatePromptModal(AppUpdateInfo info)
    {
        if (_isUpdateModalOpen) return;
        _isUpdateModalOpen = true;
        try
        {
            var dialog = new Window
        {
            Title = $"🚀 Bản Cập Nhật Mới - Chép Thẻ Nhanh Pro v{info.LatestVersion}",
            Width = 560,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(18, 16, 18, 16),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10)
        };

        var mainGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        // Header
        var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14), Spacing = 6 };
        var badgeBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(40, 56, 189, 248)),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3)
        };
        badgeBorder.Child = new TextBlock
        {
            Text = "🎉 PHÁT HIỆN PHIÊN BẢN MỚI",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
        };
        headerPanel.Children.Add(badgeBorder);

        headerPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Title) ? $"Chép Thẻ Nhanh Pro v{info.LatestVersion}" : info.Title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F8FAFC"))
        });

        var verCompareStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        verCompareStack.Children.Add(new TextBlock
        {
            Text = $"Đang dùng: v{UpdateService.CurrentVersion}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
        });
        verCompareStack.Children.Add(new TextBlock
        {
            Text = "➜",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        });
        verCompareStack.Children.Add(new TextBlock
        {
            Text = $"Bản mới: v{info.LatestVersion}",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        });
        if (!string.IsNullOrWhiteSpace(info.ReleaseDate))
        {
            verCompareStack.Children.Add(new TextBlock
            {
                Text = $" • Ngày: {info.ReleaseDate}",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
            });
        }
        headerPanel.Children.Add(verCompareStack);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        // Content / Changelog Box
        var changelogBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            BorderBrush = new SolidColorBrush(Color.Parse("#252E42")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var changelogStack = new StackPanel { Spacing = 6 };
        changelogStack.Children.Add(new TextBlock
        {
            Text = "📋 Điểm mới trong bản cập nhật này:",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FBBF24"))
        });

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 135
        };
        scrollViewer.Content = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Changelog) ? "• Cải thiện hiệu năng và tối ưu hóa hệ thống." : info.Changelog,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"))
        };
        changelogStack.Children.Add(scrollViewer);
        changelogBorder.Child = changelogStack;
        Grid.SetRow(changelogBorder, 1);
        mainGrid.Children.Add(changelogBorder);

        // Bottom Section: Progress + Buttons
        var bottomStack = new StackPanel { Spacing = 8 };

        // Download Progress Area
        var progressContainer = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            IsVisible = false
        };
        var progressStack = new StackPanel { Spacing = 6 };
        var lblProgressStatus = new TextBlock
        {
            Text = "⏳ Đang kết nối máy chủ tải về...",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            FontWeight = FontWeight.SemiBold
        };
        var downloadProgressBar = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            Background = new SolidColorBrush(Color.Parse("#1E293B"))
        };
        progressStack.Children.Add(lblProgressStatus);
        progressStack.Children.Add(downloadProgressBar);
        progressContainer.Child = progressStack;
        bottomStack.Children.Add(progressContainer);

        // Footer buttons
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };

        var authorHint = new TextBlock
        {
            Text = "Tác giả: Hoàng Điệp",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#64748B"))
        };
        Grid.SetColumn(authorHint, 0);
        footerGrid.Children.Add(authorHint);

        var btnOpenBrowser = new Button
        {
            Content = "🌐 Mở Web",
            Classes = { "actionBtn" },
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 7, 10, 7)
        };
        btnOpenBrowser.Click += (_, _) =>
        {
            string url = !string.IsNullOrWhiteSpace(info.DownloadUrlMac) ? info.DownloadUrlMac : UpdateService.GitHubRepoUrl;
            PlatformHelper.OpenUrl(url);
        };
        Grid.SetColumn(btnOpenBrowser, 1);
        footerGrid.Children.Add(btnOpenBrowser);

        var btnRemindLater = new Button
        {
            Content = "Để Sau",
            Classes = { "actionBtn" },
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 7, 14, 7)
        };
        btnRemindLater.Click += (_, _) => dialog.Close();
        Grid.SetColumn(btnRemindLater, 2);
        footerGrid.Children.Add(btnRemindLater);

        var btnAutoDownloadNow = new Button
        {
            Content = "🚀 TỰ ĐỘNG TẢI VỀ",
            Classes = { "primaryBtn" },
            Padding = new Thickness(18, 7, 18, 7),
            FontWeight = FontWeight.Bold
        };
        btnAutoDownloadNow.Click += async (_, _) =>
        {
            btnAutoDownloadNow.IsEnabled = false;
            btnRemindLater.IsEnabled = false;
            btnOpenBrowser.IsEnabled = false;
            progressContainer.IsVisible = true;
            lblProgressStatus.Text = "⏳ Đang kết nối máy chủ tải về...";
            downloadProgressBar.IsIndeterminate = true;

            string downloadUrl = !string.IsNullOrWhiteSpace(info.DownloadUrlMac) ? info.DownloadUrlMac : $"{UpdateService.GitHubRepoUrl}/releases/latest";
            if (!downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string resolved = await _updateService.ResolveMacZipUrlAsync(downloadUrl);
                    if (!string.IsNullOrWhiteSpace(resolved)) downloadUrl = resolved;
                }
                catch { }
            }

            string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string saveZipPath = Path.Combine(userDownloads, $"ChepTheNhanhPro_MacOS_v{info.LatestVersion}.zip");

            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ChepTheNhanhPro-Updater/1.0");

                using var response = await client.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                downloadProgressBar.IsIndeterminate = false;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(saveZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            double pct = (double)totalRead / totalBytes * 100.0;
                            double speedMB = (totalRead / (1024.0 * 1024.0)) / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                            downloadProgressBar.Value = pct;
                            lblProgressStatus.Text = $"Đang tải: {CopyEngineService.FormatBytes(totalRead)} / {CopyEngineService.FormatBytes(totalBytes)} ({pct:F0}%) - {speedMB:F1} MB/s";
                        }
                        else
                        {
                            lblProgressStatus.Text = $"Đang tải: {CopyEngineService.FormatBytes(totalRead)}...";
                        }
                    }

                    await fileStream.FlushAsync();
                }

                downloadProgressBar.Value = 100;
                lblProgressStatus.Text = "🎉 Tải hoàn tất 100%! Đang mở thư mục Downloads...";
                await Task.Delay(800);

                PlatformHelper.OpenFolder(userDownloads);
                dialog.Close();
            }
            catch (Exception ex)
            {
                lblProgressStatus.Text = $"⚠ Lỗi tải: {ex.Message}";
                downloadProgressBar.IsIndeterminate = false;
                btnAutoDownloadNow.IsEnabled = true;
                btnRemindLater.IsEnabled = true;
                btnOpenBrowser.IsEnabled = true;
                PlatformHelper.OpenUrl(downloadUrl);
            }
        };

        Grid.SetColumn(btnAutoDownloadNow, 3);
        footerGrid.Children.Add(btnAutoDownloadNow);

        bottomStack.Children.Add(footerGrid);
        Grid.SetRow(bottomStack, 2);
        mainGrid.Children.Add(bottomStack);

        rootBorder.Child = mainGrid;
        dialog.Content = rootBorder;
        await dialog.ShowDialog(this);
        }
        finally
        {
            _isUpdateModalOpen = false;
        }
    }

    private async void OnStartIngestClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.ClientName))
        {
            var warn = new Window
            {
                Title = "Thiếu Tên Khách",
                Width = 380,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#0F1117")),
                Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
            };
            var wStack = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            wStack.Children.Add(new TextBlock
            {
                Text = "⚠ Bạn chưa nhập Tên Khách Hàng!\nVui lòng nhập tên khách trước khi bắt đầu copy.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#FBBF24"))
            });
            var wBtn = new Button
            {
                Content = "OK",
                Classes = { "primaryBtn" },
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(22, 6, 22, 6)
            };
            wBtn.Click += (_, _) => warn.Close();
            wStack.Children.Add(wBtn);
            warn.Content = wStack;
            await warn.ShowDialog(this);
            return;
        }

        await _viewModel.StartIngestJobCommand.ExecuteAsync(null);
    }

    private void OnJobFinished(object? sender, IngestJobSummary summary)
    {
        string sizeStr = CopyEngineService.FormatBytes(summary.TotalBytesVerified);
        string ejectStatus = summary.WasEjected ? "Đã Eject thẻ nhớ an toàn (diskutil eject)." : "Dữ liệu đã lưu an toàn trên ổ cứng.";
        ShowCompletionModal(summary, sizeStr, ejectStatus);
    }

    private void ShowCompletionModal(IngestJobSummary summary, string sizeStr, string ejectStatus)
    {
        var dialog = new Window
        {
            Title = "Thông Báo Hoàn Tất Copy",
            Width = 500,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1117")),
            Foreground = new SolidColorBrush(Color.Parse("#E5E9F0"))
        };

        var rootBorder = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.Parse("#161922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#10B981")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6)
        };

        var stack = new StackPanel { Spacing = 12 };

        stack.Children.Add(new TextBlock
        {
            Text = "✅ ĐÃ COPY & VERIFY xxHash64 HOÀN TẤT!",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#34D399"))
        });

        string details =
            $"• Số file xác thực 100% : {summary.VerifiedCount} / {summary.FileResults.Count} file (Lỗi: {summary.SkippedOrFailedCount})\n" +
            $"• Tổng dung lượng       : {sizeStr} ({summary.Duration:hh\\:mm\\:ss} • TB {summary.AverageSpeedMBps:F1} MB/s)\n" +
            $"• Khách hàng / Job      : {summary.Request.ClientName} ({summary.Request.JobType})\n" +
            $"• Thư mục lưu trữ       : {summary.PrimaryJobDirectory}\n" +
            $"• Trạng thái thẻ nhớ    : {ejectStatus}";

        stack.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 12,
            LineHeight = 20,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var btnOpenFolder = new Button
        {
            Content = "Mở Thư Mục Ảnh (Finder)",
            Classes = { "actionBtn" },
            Padding = new Thickness(12, 6, 12, 6)
        };
        btnOpenFolder.Click += (_, _) =>
        {
            _viewModel.OpenDestinationFolder();
            dialog.Close();
        };

        var btnOpenReport = new Button
        {
            Content = "Xem Báo Cáo Log",
            Classes = { "actionBtn" },
            Padding = new Thickness(12, 6, 12, 6)
        };
        btnOpenReport.Click += (_, _) => _viewModel.OpenLastReport();

        var btnOk = new Button
        {
            Content = "Đóng (OK)",
            Classes = { "primaryBtn" },
            Padding = new Thickness(18, 6, 18, 6)
        };
        btnOk.Click += (_, _) => dialog.Close();

        btnPanel.Children.Add(btnOpenFolder);
        btnPanel.Children.Add(btnOpenReport);
        btnPanel.Children.Add(btnOk);
        stack.Children.Add(btnPanel);

        rootBorder.Child = stack;
        dialog.Content = rootBorder;
        dialog.ShowDialog(this);
    }
}

