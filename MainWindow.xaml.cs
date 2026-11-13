using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DocumentGenerator.Domain;
using DocumentGenerator.Infrastructure;
using DocumentGenerator.Services;

namespace DocumentGenerator;

public partial class MainWindow : Window
{
    private static readonly Color AccentColor = Color.FromRgb(0x0E, 0x61, 0x78);

    private readonly TemplateCache _cache = new();
    private readonly TemplateService _templates;
    private readonly DocxTemplateEngine _engine = new();
    private readonly VariableStore _variableStore = new();

    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FitTextBlock> _labelBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fieldOrder = new();
    private readonly Dictionary<string, Border> _fieldCards = new(StringComparer.OrdinalIgnoreCase);

    private TemplateRecord? _selected;
    private bool _loading;
    private Point _dragStart;
    private Border? _dragCard;
    private string? _dragKey;

    public MainWindow()
    {
        _templates = new TemplateService(_cache);
        InitializeComponent();
        UpdateResponsiveUi(Width, Height);

        // Show the shell first; load catalog on the next dispatcher idle tick
        // so the window paints before SQLite / file I/O runs.
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        try
        {
            // Yield once so the first frame can render.
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            await LoadTemplatesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("First load failed", ex);
            ShowError("Не вдалося завантажити шаблони", ex);
        }
    }

    private static Geometry GetIcon(string key) => (Geometry)Application.Current.FindResource(key);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // Із WindowChrome розгорнуте вікно виходить за межі екрана на товщину рамки — компенсуємо відступом.
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxIcon.Data = GetIcon(maximized ? "IconRestore" : "IconMaximize");
    }

    private void SetInfo(string text)
    {
        TemplateInfo.Text = text;
        TemplateInfoChip.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadTemplatesAsync(int? selectId = null)
    {
        try
        {
            _loading = true;
            var items = await _templates.GetAllAsync(SearchBox.Text);
            TemplatesList.ItemsSource = items;
            var selected = items.FirstOrDefault(x => x.Id == (selectId ?? _selected?.Id));
            TemplatesList.SelectedItem = selected ?? (items.Count > 0 ? items[0] : null);
            if (TemplatesList.SelectedItem is null) ShowNoSelection();
        }
        catch (Exception ex) { ShowError("Не вдалося завантажити шаблони", ex); }
        finally { _loading = false; }
    }

    private async void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Оберіть Word-шаблон",
            Filter = "Word documents (*.docx)|*.docx",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var record = await _templates.ImportAsync(dialog.FileName);
            await LoadTemplatesAsync(record.Id);
            ConfirmWindow.Notify(this, "Шаблон імпортовано", $"Шаблон «{record.Name}» додано до бібліотеки.");
        }
        catch (Exception ex) { ShowError("Не вдалося імпортувати DOCX", ex); }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && IsLoaded) await LoadTemplatesAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Перевіряємо, чи змінився оригінал вибраного шаблону; якщо так — оновлюємо кеш і перечитуємо поля.
            if (_selected is not null)
            {
                var id = _selected.Id;
                var result = _cache.Ensure(_selected);
                if (result.Refreshed)
                {
                    SaveCurrentState();
                    TemplatesList.SelectedItem = null; // скидає вибір, щоб поля завантажилися наново
                    await LoadTemplatesAsync(id);
                    ConfirmWindow.Notify(this, "Шаблон оновлено",
                        "Оригінальний файл шаблону змінився. Робочу копію оновлено, поля перечитано.");
                    return;
                }
            }
            await LoadTemplatesAsync();
        }
        catch (Exception ex) { ShowError("Не вдалося оновити шаблони", ex); }
    }

    private async void TemplateRenamePencil_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not TemplateRecord record) return;

        var dialog = new TemplateEditWindow(record.Name, record.Category) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _templates.RenameAsync(record.Id, dialog.TemplateName, dialog.Category);
            await LoadTemplatesAsync(record.Id);
        }
        catch (Exception ex) { ShowError("Не вдалося зберегти налаштування шаблону", ex); }
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is not null && _inputs.Count > 0)
            SaveCurrentState();

        _selected = TemplatesList.SelectedItem as TemplateRecord;
        ClearFieldUi();

        if (_selected is null) { ShowNoSelection(); return; }

        try
        {
            SelectedTemplateTitle.Text = _selected.Name;
            var cached = _cache.Ensure(_selected);
            var keys = _engine.FindPlaceholders(cached.Path);
            var savedValues = _variableStore.LoadValues(_selected.Id);
            var savedLabels = _variableStore.LoadLabels(_selected.Id);
            var savedOrder = _variableStore.LoadOrder(_selected.Id);

            // Порядок: збережений, потім нові ключі
            _fieldOrder.Clear();
            foreach (var k in savedOrder)
                if (keys.Contains(k, StringComparer.OrdinalIgnoreCase) &&
                    !_fieldOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                    _fieldOrder.Add(k);
            foreach (var k in keys)
                if (!_fieldOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                    _fieldOrder.Add(k);

            SetInfo($"Категорія: {_selected.Category} • Змінних: {_fieldOrder.Count}" +
                    (cached.OriginalMissing ? " • оригінал недоступний, працюємо з копією" : ""));

            if (_fieldOrder.Count == 0)
            {
                FieldsPanel.Children.Add(new TextBlock
                {
                    Text = "У шаблоні немає змінних.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 4, 0, 12)
                });
                return;
            }

            foreach (var key in _fieldOrder)
            {
                var display = savedLabels.TryGetValue(key, out var lbl) && !string.IsNullOrWhiteSpace(lbl)
                    ? lbl
                    : Humanize(key);
                _labels[key] = display;

                var value = savedValues.TryGetValue(key, out var v) ? v : "";
                var card = BuildFieldCard(key, display, value);
                _fieldCards[key] = card;
                FieldsPanel.Children.Add(card);
            }
        }
        catch (Exception ex) { ShowError("Не вдалося прочитати шаблон", ex); }
    }

    private Border BuildFieldCard(string key, string display, string value)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Tag = key,
            AllowDrop = true
        };
        card.SetResourceReference(FrameworkElement.MarginProperty, "FieldMargin");

        var root = new StackPanel();

        // Label row: text + pencil
        var labelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var editBtn = MakeIconButton("IconPencil", "Змінити назву поля");
        editBtn.Tag = key;
        editBtn.Click += EditLabel_Click;
        editBtn.Foreground = new SolidColorBrush(AccentColor);
        DockPanel.SetDock(editBtn, Dock.Right);
        labelRow.Children.Add(editBtn);

        var labelBlock = new FitTextBlock
        {
            Text = display,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _labelBlocks[key] = labelBlock;
        labelRow.Children.Add(labelBlock);
        root.Children.Add(labelRow);

        // Input row: drag handle + textbox + up/down
        var inputRow = new DockPanel();

        var handle = new FluentIcon
        {
            Data = GetIcon("IconGrip"),
            Width = 16,
            Height = 20,
            StrokeThickness = 2.4,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.SizeAll
        };
        DockPanel.SetDock(handle, Dock.Left);
        handle.PreviewMouseLeftButtonDown += FieldCard_PreviewMouseLeftButtonDown;
        handle.MouseMove += FieldCard_MouseMove;
        inputRow.Children.Add(handle);

        var arrows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(arrows, Dock.Right);

        var upBtn = MakeIconButton("IconChevronUp", "Вгору", 12);
        upBtn.Tag = key;
        upBtn.Click += MoveUp_Click;
        upBtn.Padding = new Thickness(4, 1, 4, 1);
        upBtn.MinHeight = 18;

        var downBtn = MakeIconButton("IconChevronDown", "Вниз", 12);
        downBtn.Tag = key;
        downBtn.Click += MoveDown_Click;
        downBtn.Padding = new Thickness(4, 1, 4, 1);
        downBtn.MinHeight = 18;

        arrows.Children.Add(upBtn);
        arrows.Children.Add(downBtn);
        inputRow.Children.Add(arrows);

        var input = new TextBox
        {
            Tag = key,
            AcceptsReturn = IsMultilineKey(key),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = value
        };
        AutomationProperties.SetName(input, display);
        input.SetResourceReference(FrameworkElement.MinHeightProperty, IsMultilineKey(key) ? "InputMinHMulti" : "InputMinH");
        DataObject.AddPastingHandler(input, Input_Pasting);
        _inputs[key] = input;
        inputRow.Children.Add(input);

        root.Children.Add(inputRow);
        card.Child = root;

        card.DragOver += FieldsPanel_DragOver;
        card.Drop += FieldsPanel_Drop;
        card.PreviewMouseLeftButtonDown += FieldCard_PreviewMouseLeftButtonDown;
        card.MouseMove += FieldCard_MouseMove;

        return card;
    }

    private static Button MakeIconButton(string iconKey, string tip, double iconSize = 16)
    {
        var button = new Button
        {
            Content = new FluentIcon { Data = GetIcon(iconKey), Width = iconSize, Height = iconSize },
            ToolTip = tip,
            Padding = new Thickness(4),
            Margin = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x5A))
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.SetResourceReference(FrameworkElement.MinHeightProperty, "IconSize");
        button.SetResourceReference(FrameworkElement.MinWidthProperty, "IconSize");
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private void FieldCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCard = FindParentCard(e.OriginalSource as DependencyObject);
        _dragKey = _dragCard?.Tag as string;
    }

    private void FieldCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCard is null || _dragKey is null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Не починати drag, якщо тягнуть з TextBox
        if (FindParent<TextBox>(e.OriginalSource as DependencyObject) is not null)
            return;

        DragDrop.DoDragDrop(_dragCard, new DataObject("FieldKey", _dragKey), DragDropEffects.Move);
        _dragCard = null;
        _dragKey = null;
    }

    private void FieldsPanel_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("FieldKey"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    private static void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) &&
            !e.DataObject.GetDataPresent(DataFormats.Text))
            return;

        var raw = (e.DataObject.GetData(DataFormats.UnicodeText) as string)
                  ?? (e.DataObject.GetData(DataFormats.Text) as string);
        if (string.IsNullOrEmpty(raw)) return;

        // Перетворюємо дані скопійовані з таблиці (переноси рядків і табуляції) в один рядок
        if (raw.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            var lines = raw
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => string.Join(" ",
                    line.Split('\t')
                        .Select(cell => cell.Trim())
                        .Where(cell => cell.Length > 0)))
                .Where(line => line.Length > 0);

            var singleLine = string.Join(" ", lines);

            e.CancelCommand();
            var selStart = tb.SelectionStart;
            var selLength = tb.SelectionLength;
            var current = tb.Text ?? string.Empty;
            tb.Text = current.Remove(selStart, selLength).Insert(selStart, singleLine);
            tb.CaretIndex = selStart + singleLine.Length;
        }
    }

    private void FieldsPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FieldKey")) return;
        if (e.Data.GetData("FieldKey") is not string sourceKey) return;

        string? targetKey = null;
        Border? targetCard = FindParentCard(e.OriginalSource as DependencyObject);

        // Якщо кинули у проміжок між картками (на сам FieldsPanel), визначаємо картку за Y-координатою курсора
        if (targetCard is null && _fieldOrder.Count > 0)
        {
            var dropPos = e.GetPosition(FieldsPanel);
            foreach (var key in _fieldOrder)
            {
                if (_fieldCards.TryGetValue(key, out var c))
                {
                    var topLeft = c.TransformToAncestor(FieldsPanel).Transform(new Point(0, 0));
                    if (dropPos.Y <= topLeft.Y + c.ActualHeight)
                    {
                        targetCard = c;
                        break;
                    }
                }
            }
            targetCard ??= _fieldCards.GetValueOrDefault(_fieldOrder[^1]);
        }

        if (targetCard?.Tag is string tk) targetKey = tk;

        if (targetKey is null || string.Equals(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase))
            return;

        var from = _fieldOrder.FindIndex(k => string.Equals(k, sourceKey, StringComparison.OrdinalIgnoreCase));
        var to = _fieldOrder.FindIndex(k => string.Equals(k, targetKey, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0) return;

        // Визначаємо, чи курсор у нижній половині цільової картки
        var insertAfter = false;
        if (targetCard is not null && targetCard.ActualHeight > 0)
        {
            var posInTarget = e.GetPosition(targetCard);
            insertAfter = posInTarget.Y > targetCard.ActualHeight / 2.0;
        }

        _fieldOrder.RemoveAt(from);
        to = _fieldOrder.FindIndex(k => string.Equals(k, targetKey, StringComparison.OrdinalIgnoreCase));
        if (to < 0)
        {
            _fieldOrder.Add(sourceKey);
        }
        else
        {
            var insertIndex = insertAfter ? to + 1 : to;
            if (insertIndex >= _fieldOrder.Count) _fieldOrder.Add(sourceKey);
            else _fieldOrder.Insert(insertIndex, sourceKey);
        }

        RebuildFieldsPanelPreserveValues();
        PersistOrder();
        e.Handled = true;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        var idx = _fieldOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (idx <= 0) return;
        (_fieldOrder[idx - 1], _fieldOrder[idx]) = (_fieldOrder[idx], _fieldOrder[idx - 1]);
        RebuildFieldsPanelPreserveValues();
        PersistOrder();
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        var idx = _fieldOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx >= _fieldOrder.Count - 1) return;
        (_fieldOrder[idx + 1], _fieldOrder[idx]) = (_fieldOrder[idx], _fieldOrder[idx + 1]);
        RebuildFieldsPanelPreserveValues();
        PersistOrder();
    }

    private void RebuildFieldsPanelPreserveValues()
    {
        // snapshot values
        var values = _inputs.ToDictionary(x => x.Key, x => x.Value.Text ?? "", StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(_labels, StringComparer.OrdinalIgnoreCase);

        ClearFieldUi(keepOrder: true);

        foreach (var key in _fieldOrder)
        {
            var display = labels.TryGetValue(key, out var lbl) ? lbl : Humanize(key);
            _labels[key] = display;
            var value = values.TryGetValue(key, out var v) ? v : "";
            var card = BuildFieldCard(key, display, value);
            _fieldCards[key] = card;
            FieldsPanel.Children.Add(card);
        }
    }

    private void PersistOrder()
    {
        if (_selected is null) return;
        _variableStore.SaveOrder(_selected.Id, _fieldOrder);
    }

    private void EditLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        if (_selected is null) return;

        var current = _labels.TryGetValue(key, out var l) ? l : Humanize(key);
        var dialog = new FieldLabelWindow(current, key) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _labels[key] = dialog.LabelResult;
        if (_labelBlocks.TryGetValue(key, out var block))
            block.Text = dialog.LabelResult;

        _variableStore.SaveLabels(_selected.Id, _labels);
    }

    private void ClearFieldUi(bool keepOrder = false)
    {
        _inputs.Clear();
        _labels.Clear();
        _labelBlocks.Clear();
        _fieldCards.Clear();
        if (!keepOrder) _fieldOrder.Clear();
        FieldsPanel.Children.Clear();
    }

    private void ShowNoSelection()
    {
        _selected = null;
        ClearFieldUi();
        SelectedTemplateTitle.Text = "Оберіть шаблон";
        SetInfo("");
    }

    private static string Humanize(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "(порожня змінна)";
        var spaced = System.Text.RegularExpressions.Regex.Replace(key, "([a-zа-яіїєґ])([A-ZА-ЯІЇЄҐ])", "$1 $2");
        return spaced.Replace('_', ' ');
    }

    private static bool IsMultilineKey(string key) =>
        key.Contains("address", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("адреса", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("comment", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("коментар", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("note", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("text", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("текст", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("description", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("опис", StringComparison.OrdinalIgnoreCase);

    private static Border? FindParentCard(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is Border b && b.Tag is string)
                return b;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T t) return t;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            // Відкриваємо окрему копію з кешу, а не оригінал: Word не блокує файл, з яким працює програма.
            Process.Start(new ProcessStartInfo(_cache.PrepareViewCopy(_selected))
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowError("Не вдалося відкрити файл", ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!ConfirmWindow.Ask(this, "Видалити шаблон?",
                $"Шаблон «{_selected.Name}» буде видалено з бібліотеки. Файл шаблону буде переміщено до папки backups.",
                "Видалити", ConfirmKind.Danger, "IconTrash", "IconTrash"))
            return;
        try
        {
            await _templates.DeleteAsync(_selected.Id);
            await LoadTemplatesAsync();
        }
        catch (Exception ex) { ShowError("Не вдалося видалити шаблон", ex); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _templates.BackupAllAsync();
            ConfirmWindow.Notify(this, "Резервну копію створено", $"Папка з копією:\n{AppPaths.Backups}");
        }
        catch (Exception ex) { ShowError("Не вдалося створити резервну копію", ex); }
    }

    private void ClearFields_Click(object sender, RoutedEventArgs e)
    {
        var filled = _inputs.Values.Count(x => !string.IsNullOrEmpty(x.Text));
        if (filled == 0) return;
        if (!ConfirmWindow.Ask(this, "Очистити поля?",
                $"Усі введені значення ({filled}) буде стерто. Сам шаблон залишиться без змін.",
                "Очистити", ConfirmKind.Primary, "IconEraser", "IconEraser"))
            return;
        foreach (var input in _inputs.Values) input.Clear();
        if (_selected is not null) SaveCurrentState();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetValues(out var values)) return;

        try
        {
            SaveCurrentState();
            var templatePath = _cache.Ensure(_selected!).Path;
            var previewDir = Path.Combine(AppPaths.Exports, "preview");
            Directory.CreateDirectory(previewDir);

            var previewName = $"preview_{SafeFileName(_selected.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.docx";
            var previewPath = Path.Combine(previewDir, previewName);

            _engine.Generate(templatePath, previewPath, values);
            Process.Start(new ProcessStartInfo(previewPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Помилка перегляду", ex); }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetValues(out var values)) return;

        var suggested = $"{SafeFileName(_selected!.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.docx";
        var choice = new SaveChoiceWindow(suggested) { Owner = this };
        if (choice.ShowDialog() != true || choice.Result == SaveChoiceWindow.SaveMode.Cancel)
            return;

        string targetPath;
        if (choice.Result == SaveChoiceWindow.SaveMode.AutoName)
        {
            Directory.CreateDirectory(AppPaths.Exports);
            targetPath = Path.Combine(AppPaths.Exports, suggested);
        }
        else
        {
            var dialog = new SaveFileDialog
            {
                Title = "Зберегти сформований документ",
                Filter = "Word document (*.docx)|*.docx",
                FileName = suggested,
                DefaultExt = ".docx",
                InitialDirectory = AppPaths.Exports
            };
            if (dialog.ShowDialog() != true) return;
            targetPath = dialog.FileName;
        }

        try
        {
            SaveCurrentState();
            var originalPath = AppPaths.ResolveTemplate(_selected.RelativePath);
            var templatePath = _cache.Ensure(_selected).Path;

            if (string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(templatePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Не можна зберігати результат поверх оригінального шаблону.");
            }

            await Task.Run(() => _engine.Generate(templatePath, targetPath, values));

            var open = MessageBox.Show(
                $"Документ збережено:\n{targetPath}\n\nВідкрити файл?",
                "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Помилка генерації документа", ex); }
    }

    private bool TryGetValues(out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_selected is null)
        {
            MessageBox.Show("Спочатку оберіть шаблон.", "Увага",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_inputs.Count == 0)
        {
            MessageBox.Show("У шаблоні немає змінних для заповнення.", "Увага",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        values = _inputs.ToDictionary(x => x.Key, x => x.Value.Text ?? "", StringComparer.OrdinalIgnoreCase);
        var missing = values.Where(x => string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key).ToList();
        if (missing.Count > 0)
        {
            var names = missing.Select(k => _labels.TryGetValue(k, out var n) ? n : k);
            var result = MessageBox.Show(
                $"Не заповнені поля:\n{string.Join("\n", names)}\n\nПродовжити з порожніми значеннями?",
                "Перевірка даних", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return false;
        }
        return true;
    }

    private void SaveCurrentState()
    {
        if (_selected is null) return;
        if (_inputs.Count > 0)
        {
            var values = _inputs.ToDictionary(x => x.Key, x => x.Value.Text ?? "", StringComparer.OrdinalIgnoreCase);
            _variableStore.SaveValues(_selected.Id, values);
        }
        if (_labels.Count > 0)
            _variableStore.SaveLabels(_selected.Id, _labels);
        if (_fieldOrder.Count > 0)
            _variableStore.SaveOrder(_selected.Id, _fieldOrder);
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static void ShowError(string title, Exception ex)
    {
        AppLog.Write(title, ex);

        MessageBox.Show(
            $"{title}.\n{ex.Message}\n\nДеталі записано в logs/application.log.",
            "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
