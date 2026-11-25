using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PistolLauncher;

public sealed class GameVersion
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string TypeLabel => Type switch { "release" => "Релиз", "snapshot" => "Снапшот", "old_beta" => "Старая бета", "old_alpha" => "Старая альфа", _ => Type };
    public string Status { get; set; } = "Не установлена";
    public Brush StatusBrush => Status == "Установлена" ? Brushes.LightGreen : Brushes.Gray;
}

public sealed class ModProject : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "mod";
    public string Source { get; init; } = "Modrinth";
    public string IconUrl { get; init; } = "";
    public long Downloads { get; init; }
    public string DownloadsLabel => Downloads.ToString("N0");
    private ImageSource? _avatar;
    public ImageSource? Avatar { get => _avatar; set { if (ReferenceEquals(_avatar, value)) return; _avatar = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class GameInstance
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "Vanilla";
    public ImageSource? Avatar { get; init; }
    public string Summary => string.Join(" · ", new[] { GameVersion, System.IO.Path.GetFileName(Path) }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string LoaderLabel => Loader;
}

public sealed class CatalogTargetOption
{
    public string Name { get; init; } = "";
    public GameInstance? Instance { get; init; }
    public bool IsShared { get; init; }
}

public sealed class LauncherProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Player";
    public string Type { get; set; } = "offline";
    public string SkinPath { get; set; } = "";
    [JsonIgnore] public ImageSource? Avatar { get; set; }
    [JsonIgnore] public bool English { get; set; }
    public string TypeLabel => Type switch { "ely" => "Ely.by", "microsoft" => "Microsoft", _ => English ? "Offline" : "Офлайн" };
    public string DisplayName => Name + " · " + TypeLabel;
}

public sealed class ProfileChoice
{
    public LauncherProfile? Profile { get; init; }
    public string Label { get; init; } = "";
    public ImageSource? Avatar { get; init; }
    public bool IsSettings { get; init; }
}

public sealed class ProfileStore
{
    public string ActiveProfileId { get; set; } = "";
    public List<LauncherProfile> Profiles { get; set; } = new();
}

public sealed class ElyOAuthData
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public long ExpiresAt { get; set; }
}

public sealed class LauncherPreferences
{
    public string Language { get; set; } = "ru";
    public string Accent { get; set; } = "#C4FF55";
    public string MinecraftPath { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int RamGb { get; set; } = 4;
}

public partial class MainWindow : Window
{
    private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim AvatarDownloadGate = new(4, 4);
    private readonly ObservableCollection<GameVersion> _versions = new();
    private readonly ObservableCollection<GameInstance> _instances = new();
    private readonly ObservableCollection<CatalogTargetOption> _catalogTargets = new();
    private readonly ObservableCollection<LauncherProfile> _profiles = new();
    private readonly ObservableCollection<ProfileChoice> _profileChoices = new();
    private readonly ObservableCollection<ModProject> _catalogItems = new();
    private readonly Dictionary<string, Process> _runningInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _launcherRoot = Path.Combine(AppContext.BaseDirectory, "PistolLauncher");
    private string _root;
    private readonly string _preferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher", "settings.json");
    private LauncherPreferences _preferences = new();
    private Color _activeAccent = Color.FromRgb(196, 255, 85);
    private readonly string _profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher", "offline-profile.txt");
    private readonly string _activeAccountPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher", "active-account.txt");
    private readonly string _profilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher", "profiles.json");
    private string _activeProfileId = "";
    private LauncherProfile? _activeProfile;
    private bool _loadingProfiles;
    private bool IsEnglish => string.Equals(_preferences.Language, "en", StringComparison.OrdinalIgnoreCase);
    private string L(string russian, string english) => IsEnglish ? english : russian;
    private int _skinLoadGeneration;
    private readonly Transform3DGroup _skinTransform = new();
    private AxisAngleRotation3D? _skinYawRotation;
    private AxisAngleRotation3D? _skinPitchRotation;
    private bool _skinDragging;
    private Point _skinDragStart;
    private double _skinYaw = -18, _skinPitch = 4;
    private readonly Stack<string> _fileHistory = new();
    private string _currentFileDir = "";
    private string? _curseForgeKey;
    private const string ElyRedirectUri = "http://127.0.0.1:25565/callback";
    private const string ElyWorkerLoginUrl = "https://auth.pistollauncher.workers.dev/login";
    private ElyOAuthData _elyAuth = new();
    private string? _selectedInstancePath;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _catalogFilterCts;
    private bool _catalogHasSearched;
    private readonly List<string> _catalogLastSourceErrors = new();
    private int _catalogSearchGeneration;
    private string _catalogPageQuery = "", _catalogPageType = "mod", _catalogPageVersion = "", _catalogPageLoader = "";
    private bool _catalogMoreModrinth, _catalogMoreCurseForge, _catalogLoadingMore;
    private int _catalogModrinthOffset, _catalogCurseForgeOffset;
    private GameInstance? SelectedCatalogInstance => (TargetInstance.SelectedItem as CatalogTargetOption)?.Instance;
    private bool IsCatalogSharedTarget => (TargetInstance.SelectedItem as CatalogTargetOption)?.IsShared == true;
    private GameInstance? _lastCatalogInstance;
    private string SelectedGameVersion => SelectedCatalogInstance?.GameVersion ?? _lastCatalogInstance?.GameVersion ?? "";
    private string SelectedCatalogLoader
    {
        get
        {
            var loader = (SelectedCatalogInstance ?? _lastCatalogInstance)?.Loader.Trim().ToLowerInvariant() ?? "";
            if (loader.StartsWith("neoforge", StringComparison.Ordinal)) return "neoforge";
            if (loader.StartsWith("fabric", StringComparison.Ordinal)) return "fabric";
            if (loader.StartsWith("quilt", StringComparison.Ordinal)) return "quilt";
            if (loader.StartsWith("forge", StringComparison.Ordinal)) return "forge";
            return "";
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        UpdateInstanceActionResources(_preferences.Language == "en");
        foreach (var tab in Pages.Items.OfType<TabItem>())
            DependencyPropertyDescriptor.FromProperty(TabItem.IsSelectedProperty, typeof(TabItem))?
                .AddValueChanged(tab, (_, _) => QueuePageNavigationSync());
        _preferences = LoadPreferences();
        _root = string.IsNullOrWhiteSpace(_preferences.MinecraftPath) ? Path.Combine(AppContext.BaseDirectory, "minecraft") : Path.GetFullPath(_preferences.MinecraftPath);
        SettingsMinecraftRoot.Text = _root;
        SettingsJavaPath.Text = _preferences.JavaPath;
        SettingsLauncherRoot.Text = AppContext.BaseDirectory;
        SettingsLanguage.SelectedIndex = _preferences.Language == "en" ? 1 : 0;
        SettingsAccent.SelectedIndex = _preferences.Accent.ToUpperInvariant() switch { "#55D7FF" => 1, "#C278FF" => 2, "#FF9B55" => 3, _ => 0 };
        SettingsRam.Value = Math.Clamp(_preferences.RamGb, 1, 32);
        UpdateRamLabel();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PistolLauncher/0.1 (+https://modrinth.com)");
        _curseForgeKey = SecretStore.Load();
        try { _elyAuth = JsonSerializer.Deserialize<ElyOAuthData>(SecretStore.LoadEly() ?? "") ?? new(); } catch { _elyAuth = new(); }
        InstanceList.ItemsSource = _instances;
        HomeInstanceList.ItemsSource = _instances;
        TargetInstance.ItemsSource = _catalogTargets;
        CatalogResults.ItemsSource = _catalogItems;
        ProfileList.ItemsSource = _profiles;
        ProfileSelector.ItemsSource = _profileChoices;
        LoadProfiles();
        RebuildProfileChoices();
        Directory.CreateDirectory(Path.Combine(_launcherRoot, ".assets", "skins"));
        Directory.CreateDirectory(Path.Combine(_root, "libraries"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(Path.Combine(_root, "instances"));
        Directory.CreateDirectory(Path.Combine(_root, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(_root, "shaderpacks"));
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        _currentFileDir = _root;
        RefreshFileList();
        var guide = Path.Combine(_launcherRoot, ".assets", "textures", "guide.png");
        if (File.Exists(guide)) AssistantImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(guide));
        UpdateActiveProfileUi();
        UpdateNavigation();
        ApplyPreferences();
        MigrateLegacyVersions();
        RefreshInstances();
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        Loaded += async (_, _) =>
        {
        SyncPageNavigation();
            await LoadVersionsAsync();
        };
    }

    private async Task LoadVersionsAsync()
    {
            StatusText.Text = _preferences.Language == "en" ? "Loading official Minecraft versions…" : "Загружаю официальный список версий…";
        try
        {
            using var response = await Http.GetAsync(ManifestUrl);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var list = doc.RootElement.GetProperty("versions");
            _versions.Clear();
            foreach (var item in list.EnumerateArray())
            {
                var v = new GameVersion { Id = item.GetProperty("id").GetString() ?? "", Type = item.GetProperty("type").GetString() ?? "", Url = item.GetProperty("url").GetString() ?? "" };
                _versions.Add(v);
            }
            StatusText.Text = _preferences.Language == "en" ? $"Ready to create an instance · {_versions.Count} versions available" : $"Готово к созданию инстанса · в каталоге {_versions.Count} версий";
            DetailText.Text = _preferences.Language == "en" ? "Vanilla files will be installed in the selected instance folder." : "Vanilla-файлы будут установлены в папку конкретного инстанса.";
        }
        catch (Exception ex)
        {
            StatusText.Text = _preferences.Language == "en" ? "Could not load the version list" : "Не удалось загрузить каталог версий";
            DetailText.Text = ex.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();
    private void NavigateToPage(int index)
    {
        Pages.SelectedIndex = index;
        QueuePageNavigationSync();
    }
    private void VersionsNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(1);
    private void HomeNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(0);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void CatalogNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(2);
    private void ClosetNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(3);
    private void FilesNav_Click(object sender, RoutedEventArgs e) { NavigateToPage(4); RefreshFileList(); }
    private void AccountsNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(5);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => NavigateToPage(6);
    private void OpenSharedPackFolder_Click(object sender, RoutedEventArgs e)
    {
        var kind = (ProjectKind.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var folder = kind switch
        {
            "resourcepack" => Path.Combine(_root, "resourcepacks"),
            "shader" => Path.Combine(_root, "shaderpacks"),
            _ => null
        };
        if (folder is null)
        {
            CatalogStatus.Text = L("Кнопка «Общая папка» доступна для ресурспаков и шейдеров.", "The shared folder option is available for resource packs and shaders.");
            return;
        }
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        CatalogStatus.Text = L($"Общая папка: {folder}. Содержимое синхронизируется с инстансом на время игры.", $"Shared folder: {folder}. Its contents are synced to the instance while the game is running.");
    }

    private LauncherPreferences LoadPreferences()
    {
        try { return JsonSerializer.Deserialize<LauncherPreferences>(File.ReadAllText(_preferencesPath)) ?? new(); }
        catch { return new(); }
    }

    private void ApplyPreferences()
    {
        var accent = ParseAccent(_preferences.Accent);
        SetAccentTheme(accent);
        ApplyLanguage(_preferences.Language == "en");
    }

    private static Color ParseAccent(string? value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? "#C4FF55")!; }
        catch { return Color.FromRgb(196, 255, 85); }
    }

    private void SetAccentTheme(Color accent)
    {
        _activeAccent = accent;
        var brush = new SolidColorBrush(accent);
        brush.Freeze();
        Resources["AccentBrush"] = brush;
    }

    private SolidColorBrush AccentBrush => new(_activeAccent);

    private void UpdateInstanceActionResources(bool english)
    {
        Resources["InstanceOpenFolderText"] = english ? "Open folder" : "Открыть папку";
        Resources["InstanceRenameText"] = english ? "Rename" : "Переименовать";
        Resources["InstanceDeleteText"] = english ? "Delete" : "Удалить";
        Resources["InstanceCloseText"] = english ? "Close" : "Закрыть";
    }

    private void ApplyLanguage(bool english)
    {
        UpdateInstanceActionResources(english);
        NavHome.Content = english ? "⌂   Home" : "⌂   Домой";
        NavVersions.Content = english ? "◈   Instances" : "◈   Версии";
        NavCatalog.Content = english ? "⌕   Catalog" : "⌕   Каталог";
        NavCloset.Content = english ? "👕   Wardrobe" : "👕   Шкафчик";
        NavFiles.Content = english ? "▤   Minecraft" : "▤   Minecraft";
        NavSettings.Content = english ? "⚙   Settings" : "⚙   Настройки";
        AccountsNavLabel.Text = english ? "Accounts" : "Аккаунты";
        var headers = english ? new[] { "Home", "Instances", "Catalog", "Wardrobe", "Minecraft", "Accounts", "Settings" } : new[] { "Домой", "Версии", "Каталог", "Шкафчик", "Minecraft", "Аккаунты", "Настройки" };
        for (var i = 0; i < Math.Min(Pages.Items.Count, headers.Length); i++) ((TabItem)Pages.Items[i]).Header = headers[i];
        SettingsHeading.Text = english ? "Settings" : "Настройки";
        SettingsDescription.Text = english ? "Interface language, accent color, and Minecraft data location." : "Язык интерфейса, цвет акцента и расположение файлов Minecraft.";
        SettingsLanguageLabel.Text = english ? "Language" : "Язык";
        SettingsAccentLabel.Text = english ? "Accent color" : "Цвет темы";
        SettingsPathLabel.Text = english ? "Minecraft data folder" : "Папка данных Minecraft";
        SettingsPathNote.Text = english ? "The new folder will be used for downloads and instances. Existing files are not moved automatically." : "Новая папка используется для загрузок и инстансов. Существующие файлы автоматически не перемещаются.";
        SettingsAppPathLabel.Text = english ? "Launcher folder" : "Папка лаунчера";
        BrowseMinecraftRootButton.Content = english ? "Browse…" : "Выбрать…";
        SettingsJavaPathLabel.Text = english ? "Java folder (JAVA_HOME)" : "Папка Java (JAVA_HOME)";
        SettingsRamLabel.Text = english ? "Minecraft memory allocation" : "Оперативная память Minecraft";
        UpdateRamLabel();
        BrowseJavaPathButton.Content = english ? "Browse…" : "Выбрать…";
        SaveSettingsButton.Content = english ? "Save settings" : "Сохранить настройки";
        OpenMinecraftRootButton.Content = english ? "Open Minecraft folder" : "Открыть папку Minecraft";
        SetComboText(CatalogSource, english ? new[] { "All sources", "Modrinth", "CurseForge" } : new[] { "Все источники", "Modrinth", "CurseForge" });
        SetComboText(ProjectKind, english ? new[] { "Mods", "Resource packs", "Shaders" } : new[] { "Моды", "Ресурспаки", "Шейдеры" });
        SetComboText(SettingsLanguage, english ? new[] { "Russian", "English" } : new[] { "Русский", "English" });
        SetComboText(SettingsAccent, english ? new[] { "Lime", "Blue", "Purple", "Orange" } : new[] { "Лаймовый", "Голубой", "Фиолетовый", "Оранжевый" });
        var ruToEn = new Dictionary<string, string>
        {
            ["ТВОЯ ИГРА. ТВОИ ПРАВИЛА."] = "YOUR GAME. YOUR RULES.", ["С возвращением, Player"] = "Welcome back, Player",
            ["Выбери инстанс или собери свой собственный."] = "Choose an instance or create your own.", ["＋ Новая сборка"] = "＋ New instance",
            ["Твой мир начинается здесь"] = "Your world starts here", ["Создавай отдельные инстансы, скачивай моды и держи все сборки в одном месте."] = "Create separate instances, download mods, and keep your setups in one place.",
            ["Открыть мои инстансы  →"] = "Open my instances  →", ["Твои инстансы"] = "Your instances", ["Пока нет инстансов — создай первый."] = "No instances yet — create your first.",
            ["Каталог модов"] = "Mod catalog", ["Моды, ресурспаки и шейдеры из Modrinth и CurseForge."] = "Mods, resource packs, and shaders from Modrinth and CurseForge.",
            ["Искать"] = "Search", ["Ресурспаки"] = "Resource packs", ["Шейдеры"] = "Shaders", ["Моды"] = "Mods", ["Скачать"] = "Download",
            ["Шкафчик"] = "Wardrobe", ["Скины и плащи твоего профиля."] = "Skins and capes for your profile.", ["Файлы Minecraft"] = "Minecraft files",
            ["Активный профиль"] = "Active profile", ["Войти через Microsoft"] = "Sign in with Microsoft", ["Войти через Ely.by"] = "Sign in with Ely.by",
            ["Создать офлайн-профиль"] = "Create offline profile", ["Открыть папку"] = "Open folder", ["Переименовать"] = "Rename", ["Удалить"] = "Delete", ["Закрыть"] = "Close",
            ["▶ Играть"] = "▶ Play", ["＋ Добавить инстанс"] = "＋ Add instance", ["⟳ Обновить"] = "⟳ Refresh",
            ["Модификации и сборки"] = "Mods and modpacks", ["НАВИГАЦИЯ"] = "NAVIGATION", ["ПРОФИЛЬ"] = "PROFILE", ["Выбирай аккаунт в правом верхнем углу лаунчера."] = "Choose an account from the launcher menu in the upper right.",
            ["Выбери профиль в правом верхнем углу."] = "Choose a profile from the menu in the upper right.", ["Скин выбранного профиля"] = "Selected profile skin", ["Здесь будет отображаться скин активного профиля."] = "The active profile skin will appear here.",
            ["Каждая версия живёт отдельно в minecraft/instances."] = "Each instance has its own folder in minecraft/instances.", ["Загружаю каталог Minecraft…"] = "Loading Minecraft versions…", ["Нет выбранного профиля"] = "No profile selected", ["Офлайн-профиль"] = "Offline profile", ["Профили"] = "Profiles",
            ["Папка данных Minecraft"] = "Minecraft data folder", ["Папка лаунчера"] = "Launcher folder", ["Папка Java (JAVA_HOME)"] = "Java folder (JAVA_HOME)", ["Цвет темы"] = "Accent color", ["Язык"] = "Language", ["Настройки"] = "Settings",
            ["Новая папка используется для загрузок и инстансов. Существующие файлы автоматически не перемещаются."] = "The new folder is used for downloads and instances. Existing files are not moved automatically.", ["Просмотр папки minecraft внутри PistolLauncher."] = "Browse the minecraft folder inside PistolLauncher.",
            ["Все источники"] = "All sources", ["Выбрать…"] = "Browse…", ["Голубой"] = "Blue", ["Лаймовый"] = "Lime", ["Оранжевый"] = "Orange", ["Фиолетовый"] = "Purple", ["Добавить профиль"] = "Add profile", ["Загрузить скин"] = "Upload skin",
            ["Настроить CurseForge API key"] = "Configure CurseForge API key", ["Открыть"] = "Open", ["Открыть папку Minecraft"] = "Open Minecraft folder", ["Сохранить настройки"] = "Save settings", ["Русский"] = "Russian", ["English"] = "English",
            ["Настройки профилей"] = "Profile settings", ["Добавить профиль Ely.by"] = "Add Ely.by profile", ["Добавить профиль Microsoft"] = "Add Microsoft profile", ["Добавить офлайн-профиль"] = "Add offline profile",
            ["Minecraft сразу завершился"] = "Minecraft exited immediately", ["Игра запущена в отдельном окне."] = "The game is running in a separate window.", ["Ожидаю авторизацию в браузере…"] = "Waiting for browser sign-in…",
            ["Общая папка"] = "Shared folder", ["Общая папка…"] = "Shared folder…", ["Версии"] = "Instances", ["Домой"] = "Home", ["Каталог"] = "Catalog", ["Аккаунты"] = "Accounts",
            ["Vanilla-файлы будут установлены в папку конкретного инстанса."] = "Vanilla files will be installed in the selected instance folder.", ["Загрузка завершена."] = "Download complete.", ["Скин загружен. Перетаскивай модель мышью, чтобы вращать."] = "Skin loaded. Drag to rotate the model.",
            ["Настройки сохранены."] = "Settings saved.", ["С возвращением, "] = "Welcome back, ", ["Скин профиля "] = "Skin for ", ["Активен: "] = "Active: ",
            ["Переименовать инстанс"] = "Rename instance", ["Переименовать профиль"] = "Rename profile", ["Удалить инстанс"] = "Delete instance", ["Открыть папку инстанса"] = "Open instance folder"
            , ["Оперативная память Minecraft"] = "Minecraft memory allocation", ["Все инстансы →"] = "All instances →", ["← Назад"] = "← Back", ["↑ Вверх"] = "↑ Up",
            ["Язык интерфейса, цвет акцента и расположение файлов Minecraft."] = "Interface language, accent color, and Minecraft file location.", ["Modrinth доступен без ключа. Для CurseForge потребуется свой API-ключ."] = "Modrinth works without a key. CurseForge requires your API key.",
            ["Готово к созданию инстанса"] = "Ready to create an instance", ["Загружаю официальный список версий…"] = "Loading official Minecraft versions…", ["Не удалось загрузить каталог версий"] = "Could not load the version list",
            ["Профиль не выбран"] = "Profile is not selected", ["Добавь или выбери профиль в верхнем списке."] = "Add or choose a profile from the menu above.", ["Для этого офлайн-профиля скин ещё не загружен."] = "This offline profile does not have a skin yet.",
            ["Предпросмотр Microsoft появится после подключения авторизации."] = "Microsoft skin preview will be available after sign-in is connected.", ["Перетаскивай модель мышью, чтобы вращать."] = "Drag the model to rotate it.", ["Выбери папку данных Minecraft"] = "Choose the Minecraft data folder", ["Выбери папку Java (JAVA_HOME)"] = "Choose the Java folder (JAVA_HOME)"
        };
        var translations = english ? ruToEn : ruToEn.GroupBy(kv => kv.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        TranslateTree(this, translations);
        // Apply explicitly localized controls after the tree pass, which may translate values in reverse.
        NavHome.Content = english ? "⌂   Home" : "⌂   Домой";
        NavVersions.Content = english ? "◈   Instances" : "◈   Версии";
        NavCatalog.Content = english ? "⌕   Catalog" : "⌕   Каталог";
        NavCloset.Content = english ? "👕   Wardrobe" : "👕   Шкафчик";
        NavFiles.Content = "▤   Minecraft";
        NavSettings.Content = english ? "⚙   Settings" : "⚙   Настройки";
        AccountsNavLabel.Text = english ? "Accounts" : "Аккаунты";
        SettingsHeading.Text = english ? "Settings" : "Настройки";
        SettingsDescription.Text = english ? "Interface language, accent color, and Minecraft data location." : "Язык интерфейса, цвет акцента и расположение файлов Minecraft.";
        SettingsLanguageLabel.Text = english ? "Language" : "Язык";
        SettingsAccentLabel.Text = english ? "Accent color" : "Цвет темы";
        SettingsPathLabel.Text = english ? "Minecraft data folder" : "Папка данных Minecraft";
        SettingsPathNote.Text = english ? "The new folder will be used for downloads and instances. Existing files are not moved automatically." : "Новая папка используется для загрузок и инстансов. Существующие файлы автоматически не перемещаются.";
        SettingsAppPathLabel.Text = english ? "Launcher folder" : "Папка лаунчера";
        SettingsJavaPathLabel.Text = english ? "Java folder (JAVA_HOME)" : "Папка Java (JAVA_HOME)";
        SettingsRamLabel.Text = english ? "Minecraft memory allocation" : "Оперативная память Minecraft";
        BrowseMinecraftRootButton.Content = english ? "Browse…" : "Выбрать…";
        BrowseJavaPathButton.Content = english ? "Browse…" : "Выбрать…";
        SaveSettingsButton.Content = english ? "Save settings" : "Сохранить настройки";
        OpenMinecraftRootButton.Content = english ? "Open Minecraft folder" : "Открыть папку Minecraft";
        SetComboText(CatalogSource, english ? new[] { "All sources", "Modrinth", "CurseForge" } : new[] { "Все источники", "Modrinth", "CurseForge" });
        SetComboText(ProjectKind, english ? new[] { "Mods", "Resource packs", "Shaders" } : new[] { "Моды", "Ресурспаки", "Шейдеры" });
        SetComboText(SettingsLanguage, english ? new[] { "Russian", "English" } : new[] { "Русский", "English" });
        SetComboText(SettingsAccent, english ? new[] { "Lime", "Blue", "Purple", "Orange" } : new[] { "Лаймовый", "Голубой", "Фиолетовый", "Оранжевый" });
        RebuildProfileChoices();
        UpdateActiveProfileUi(loadSkin: false);
        TopCrumb.Text = "PistolLauncher / " + ((Pages.SelectedItem as TabItem)?.Header?.ToString() ?? (english ? "Home" : "Домой"));
        RefreshLocalizedTransientStatus();
        QueuePageNavigationSync();
    }

    private static void TranslateTree(DependencyObject node, IReadOnlyDictionary<string, string> translations)
    {
        if (node is TextBlock text && translations.TryGetValue(text.Text, out var translatedText)) text.Text = translatedText;
        if (node is ContentControl content && content.ReadLocalValue(ContentControl.ContentProperty) is string contentText && translations.TryGetValue(contentText, out var translatedContent)) content.Content = translatedContent;
        if (node is FrameworkElement element && element.ToolTip is string tip && translations.TryGetValue(tip, out var translatedTip)) element.ToolTip = translatedTip;
        var visited = new HashSet<DependencyObject>();
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            if (visited.Add(child)) TranslateTree(child, translations);
        if (node is Visual or Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (visited.Add(child)) TranslateTree(child, translations);
            }
        }
    }

    private static void SetComboText(ComboBox combo, IReadOnlyList<string> labels)
    {
        for (var i = 0; i < Math.Min(combo.Items.Count, labels.Count); i++)
            if (combo.Items[i] is ComboBoxItem item) item.Content = labels[i];
    }

    private string FormatCatalogResultStatus(int count, IEnumerable<string> errors)
    {
        var errorText = string.Join(" ", errors.Select(error => IsEnglish
            ? error.Replace("CurseForge пропущен: добавь API key кнопкой ниже.", "CurseForge skipped: add an API key using the button below.")
            : error));
        var summary = IsEnglish
            ? $"Found {count} · Modrinth and CurseForge · Minecraft {SelectedGameVersion}."
            : $"Найдено: {count} · Modrinth и CurseForge · Minecraft {SelectedGameVersion}.";
        return string.IsNullOrWhiteSpace(errorText) ? summary : summary + " " + errorText;
    }

    private void RefreshLocalizedTransientStatus()
    {
        if (_catalogHasSearched && (CatalogStatus.Text.StartsWith("Found ", StringComparison.Ordinal) || CatalogStatus.Text.StartsWith("Найдено:", StringComparison.Ordinal)))
            CatalogStatus.Text = FormatCatalogResultStatus(_catalogItems.Count, _catalogLastSourceErrors);

        if (_activeProfile is not { } profile) return;
        var skinPath = profile.Type switch
        {
            "offline" => !string.IsNullOrWhiteSpace(profile.SkinPath) ? profile.SkinPath : Path.Combine(_launcherRoot, ".assets", "skins", profile.Id + ".png"),
            "ely" => Path.Combine(_launcherRoot, ".assets", "skins", "ely-" + profile.Id + ".png"),
            "microsoft" => Path.Combine(_launcherRoot, ".assets", "skins", "microsoft-" + profile.Id + ".png"),
            _ => ""
        };
        if (profile.Type == "ely" && File.Exists(skinPath))
            ClosetStatus.Text = L("Скин Ely.by загружен. Перетаскивай модель мышью, чтобы вращать.", "Ely.by skin loaded. Drag to rotate.");
        else if (profile.Type == "offline" && File.Exists(skinPath))
            ClosetStatus.Text = L("Локальный скин офлайн-профиля. Перетаскивай модель мышью, чтобы вращать.", "Offline skin. Drag to rotate.");
        else if (profile.Type == "offline")
            ClosetStatus.Text = L("Для этого офлайн-профиля скин ещё не загружен.", "This offline profile does not have a skin yet.");
        else if (profile.Type == "microsoft")
            ClosetStatus.Text = L("Предпросмотр Microsoft появится после подключения авторизации.", "Microsoft skin preview will be available after sign-in is connected.");
    }

    private void BrowseMinecraftRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L("Выбери папку данных Minecraft", "Choose the Minecraft data folder"), InitialDirectory = Directory.Exists(SettingsMinecraftRoot.Text) ? SettingsMinecraftRoot.Text : AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) == true) SettingsMinecraftRoot.Text = dialog.FolderName;
    }

    private void BrowseJavaPath_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SettingsJavaPath.Text) ? SettingsJavaPath.Text : Environment.GetEnvironmentVariable("JAVA_HOME");
        var dialog = new OpenFolderDialog { Title = L("Выбери папку Java (JAVA_HOME)", "Choose the Java folder (JAVA_HOME)"), InitialDirectory = !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial) ? initial : AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) == true) SettingsJavaPath.Text = dialog.FolderName;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var nextRoot = Path.GetFullPath(SettingsMinecraftRoot.Text.Trim());
            if (string.IsNullOrWhiteSpace(nextRoot)) throw new InvalidOperationException("Укажи папку данных Minecraft.");
            Directory.CreateDirectory(nextRoot);
            var javaPath = SettingsJavaPath.Text.Trim();
            if (!string.IsNullOrWhiteSpace(javaPath))
            {
                javaPath = Path.GetFullPath(javaPath);
                if (!File.Exists(javaPath) && !File.Exists(Path.Combine(javaPath, "bin", "java.exe")) && !File.Exists(Path.Combine(javaPath, "java.exe")))
                    throw new InvalidOperationException("В выбранной папке не найден java.exe (проверь папку JAVA_HOME). ");
            }
            _root = nextRoot;
            foreach (var name in new[] { "libraries", "assets", "instances", "resourcepacks", "shaderpacks", "downloads" }) Directory.CreateDirectory(Path.Combine(_root, name));
            var selectedAccent = (SettingsAccent.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#C4FF55";
            _preferences = new LauncherPreferences { Language = SettingsLanguage.SelectedIndex == 1 ? "en" : "ru", Accent = selectedAccent, MinecraftPath = _root, JavaPath = javaPath, RamGb = (int)Math.Round(SettingsRam.Value) };
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            File.WriteAllText(_preferencesPath, JsonSerializer.Serialize(_preferences, new JsonSerializerOptions { WriteIndented = true }));
            SettingsMinecraftRoot.Text = _root;
            _currentFileDir = _root;
            RefreshFileList();
            var nextAccent = ParseAccent(selectedAccent);
            SetAccentTheme(nextAccent);
            ApplyLanguage(_preferences.Language == "en");
            RefreshInstances();
            QueuePageNavigationSync();
            SettingsStatus.Text = _preferences.Language == "en" ? "Settings saved." : "Настройки сохранены.";
        }
        catch (Exception ex) { SettingsStatus.Text = "Не удалось сохранить настройки: " + ex.Message; }
    }

    private void SettingsRam_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRamLabel();
    private void UpdateRamLabel()
    {
        if (SettingsRamValue is null) return;
        var value = (int)Math.Round(SettingsRam.Value);
        SettingsRamValue.Text = _preferences.Language == "en" ? $"{value} GB" : $"{value} ГБ";
    }

    private void OpenMinecraftRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_root);
        Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
    }

    private void OpenLauncherFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true });

    private void LoadProfiles()
    {
        ProfileStore store;
        var hasReadableStore = false;
        try
        {
            if (File.Exists(_profilesPath))
            {
                store = JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(_profilesPath)) ?? new();
                hasReadableStore = true;
            }
            else store = new();
        }
        catch { store = new(); }
        _profiles.Clear();
        foreach (var profile in store.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Type is "offline" or "ely" or "microsoft")) _profiles.Add(profile);
        // Import legacy accounts only on the first run with the profile-store format.
        // Re-importing token data on every launch resurrected profiles the user had deleted.
        if (!hasReadableStore && !string.IsNullOrWhiteSpace(_elyAuth.Username))
            _profiles.Add(new LauncherProfile { Name = _elyAuth.Username, Type = "ely" });
        if (!hasReadableStore && !_profiles.Any(p => p.Type == "offline") && File.Exists(_profilePath))
        {
            var legacyName = File.ReadAllText(_profilePath).Trim();
            if (legacyName.Length > 0) _profiles.Add(new LauncherProfile { Name = legacyName, Type = "offline" });
        }
        if (_profiles.Count == 0 && !hasReadableStore) _profiles.Add(new LauncherProfile { Name = "Player", Type = "offline" });
        _activeProfileId = store.ActiveProfileId;
        if (!_profiles.Any(p => p.Id == _activeProfileId))
        {
            var preferEly = File.Exists(_activeAccountPath) && File.ReadAllText(_activeAccountPath).Trim() == "ely";
            _activeProfile = _profiles.FirstOrDefault(p => preferEly && p.Type == "ely") ?? _profiles.FirstOrDefault(p => p.Type == "offline") ?? _profiles.FirstOrDefault();
            _activeProfileId = _activeProfile?.Id ?? "";
        }
        else _activeProfile = _profiles.First(p => p.Id == _activeProfileId);
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        File.WriteAllText(_profilesPath, JsonSerializer.Serialize(new ProfileStore { ActiveProfileId = _activeProfileId, Profiles = _profiles.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RebuildProfileChoices()
    {
        _loadingProfiles = true;
        _profileChoices.Clear();
        foreach (var profile in _profiles)
        {
            profile.English = _preferences.Language == "en";
            UpdateProfileAvatar(profile);
            _profileChoices.Add(new ProfileChoice { Profile = profile, Label = profile.DisplayName, Avatar = profile.Avatar });
        }
        _profileChoices.Add(new ProfileChoice { Label = _preferences.Language == "en" ? "Profile settings" : "Настройки профилей", IsSettings = true });
        ProfileSelector.SelectedItem = _profileChoices.FirstOrDefault(c => c.Profile?.Id == _activeProfileId);
        _loadingProfiles = false;
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || ProfileSelector.SelectedItem is not ProfileChoice choice) return;
        if (choice.IsSettings)
        {
            Pages.SelectedIndex = 5;
            UpdateNavigation();
            _loadingProfiles = true;
            ProfileSelector.SelectedItem = _profileChoices.FirstOrDefault(c => c.Profile?.Id == _activeProfileId);
            _loadingProfiles = false;
            return;
        }
        if (choice.Profile is null) return;
        _activeProfile = choice.Profile;
        _activeProfileId = choice.Profile.Id;
        SaveProfiles();
        UpdateActiveProfileUi();
    }

    private void UpdateActiveProfileUi(bool loadSkin = true)
    {
        var english = _preferences.Language == "en";
        if (_activeProfile is null)
        {
            AccountStatus.Text = english ? "No active profile" : "Нет активного профиля";
            SidebarProfile.Text = english ? "No profile selected" : "Профиль не выбран";
            WelcomeText.Text = english ? "Welcome back, Player" : "С возвращением, Player";
            ClosetProfileText.Text = english ? "Choose a profile from the menu in the upper right." : "Выбери профиль в правом верхнем углу.";
            ClosetTitle.Text = english ? "No profile selected" : "Нет выбранного профиля";
            ClosetStatus.Text = english ? "Add or choose a profile from the menu above." : "Добавь или выбери профиль в верхнем списке.";
            UploadOfflineSkinButton.Visibility = Visibility.Collapsed;
            ClosetSkinViewport.Children.Clear();
            return;
        }
        var profile = _activeProfile;
        profile.English = english;
        UpdateProfileAvatar(profile);
        AccountStatus.Text = (english ? "Active: " : "Активен: ") + profile.DisplayName;
        SidebarProfile.Text = profile.DisplayName;
        WelcomeText.Text = (english ? "Welcome back, " : "С возвращением, ") + profile.Name;
        ClosetProfileText.Text = profile.DisplayName;
        ClosetTitle.Text = (english ? "Skin for " : "Скин профиля ") + profile.Name;
        UploadOfflineSkinButton.Visibility = profile.Type == "offline" ? Visibility.Visible : Visibility.Collapsed;
        ProfileList.SelectedItem = profile;
        if (loadSkin) _ = LoadSelectedSkinAsync(profile);
    }

    private async Task LoadSelectedSkinAsync(LauncherProfile profile)
    {
        var generation = Interlocked.Increment(ref _skinLoadGeneration);
        ClosetSkinViewport.Children.Clear();
        if (profile.Type == "offline")
        {
            var skinPath = !string.IsNullOrWhiteSpace(profile.SkinPath) ? profile.SkinPath : Path.Combine(_launcherRoot, ".assets", "skins", profile.Id + ".png");
            UpdateProfileAvatar(profile, File.Exists(skinPath) ? skinPath : null);
            ProfileList.Items.Refresh(); RebuildProfileChoices();
            if (File.Exists(skinPath))
            {
                try { RenderSkinModel(skinPath); ClosetStatus.Text = _preferences.Language == "en" ? "Offline skin. Drag to rotate." : "Локальный скин офлайн-профиля. Перетаскивай модель мышью, чтобы вращать."; }
                catch (Exception ex) { ClosetStatus.Text = "Не удалось открыть скин: " + ex.Message; }
            }
            else ClosetStatus.Text = _preferences.Language == "en" ? "This offline profile does not have a skin yet." : "Для этого офлайн-профиля скин ещё не загружен.";
            return;
        }
        if (profile.Type == "ely")
        {
            var cache = Path.Combine(_launcherRoot, ".assets", "skins", "ely-" + profile.Id + ".png");
            try
            {
                if (File.Exists(cache))
                {
                    try { RenderSkinModel(cache); }
                    catch { File.Delete(cache); }
                }
                if (!File.Exists(cache)) await DownloadElySkinAsync(profile.Name, cache);
                if (generation != _skinLoadGeneration) return;
                RenderSkinModel(cache);
                UpdateProfileAvatar(profile, cache);
                ProfileList.Items.Refresh(); RebuildProfileChoices();
                ClosetStatus.Text = L("Скин Ely.by загружен. Перетаскивай модель мышью, чтобы вращать.", "Ely.by skin loaded. Drag to rotate.");
            }
            catch (Exception ex) { if (generation == _skinLoadGeneration) ClosetStatus.Text = (_preferences.Language == "en" ? "Could not load the Ely.by skin: " : "Не удалось загрузить скин Ely.by: ") + ex.Message; }
            return;
        }
        ClosetStatus.Text = _preferences.Language == "en" ? "Microsoft skin preview will be available after sign-in is connected." : "Предпросмотр Microsoft появится после подключения авторизации.";
    }

    private void UpdateProfileAvatar(LauncherProfile profile, string? skinPath = null)
    {
        profile.Avatar = null;
        if (profile.Type != "offline")
        {
            skinPath ??= profile.Type == "ely"
                ? Path.Combine(_launcherRoot, ".assets", "skins", "ely-" + profile.Id + ".png")
                : Path.Combine(_launcherRoot, ".assets", "skins", "microsoft-" + profile.Id + ".png");
            if (File.Exists(skinPath))
            {
                try
                {
                    var skin = LoadBitmap(skinPath) as System.Windows.Media.Imaging.BitmapSource;
                    if (skin is not null && skin.PixelWidth >= 16 && skin.PixelHeight >= 16)
                    {
                        var face = new System.Windows.Media.Imaging.CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
                        face.Freeze(); profile.Avatar = face;
                    }
                }
                catch { }
            }
        }
        if (profile.Avatar is null)
        {
            var icon = Path.Combine(_launcherRoot, ".assets", "textures", "offline-profile.png");
            if (File.Exists(icon)) try { profile.Avatar = LoadBitmap(icon); } catch { }
        }
    }

    private async Task DownloadElySkinAsync(string username, string cachePath)
    {
        Exception? lastError = null;
        foreach (var scheme in new[] { "https", "http" })
        {
            var temporary = cachePath + ".part";
            try
            {
                var url = $"{scheme}://skinsystem.ely.by/skins/{Uri.EscapeDataString(username)}.png";
                var bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(temporary, bytes);
                var image = LoadBitmap(temporary) as System.Windows.Media.Imaging.BitmapSource ?? throw new InvalidDataException("Сервер Ely.by вернул не изображение PNG.");
                if (image.PixelWidth != 64 || image.PixelHeight is not (64 or 32)) throw new InvalidDataException("У Ely.by вернулась текстура неожиданного размера.");
                SaveAsPng(temporary, cachePath + ".write");
                File.Move(cachePath + ".write", cachePath, overwrite: true);
                return;
            }
            catch (Exception ex) { lastError = ex; }
            finally { try { File.Delete(temporary); } catch { } try { File.Delete(cachePath + ".write"); } catch { } }
        }
        throw new InvalidOperationException("Не удалось получить скин с Ely.by. Проверь, что у аккаунта установлен скин.", lastError);
    }

    private sealed record SkinUv(Rect Front, Rect Back, Rect Left, Rect Right, Rect Top, Rect Bottom);

    private void RenderSkinModel(string path)
    {
        var bitmap = LoadBitmap(path) as System.Windows.Media.Imaging.BitmapSource ?? throw new InvalidDataException("Не удалось прочитать PNG скина.");
        if (bitmap.PixelWidth != 64 || bitmap.PixelHeight is not (64 or 32)) throw new InvalidDataException("Текстура должна иметь размер 64×64 или 64×32 пикселя.");
        bitmap.Freeze();
        var skin = new Model3DGroup();
        skin.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
        skin.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -3)));
        skin.Children.Add(new DirectionalLight(Color.FromRgb(120, 120, 120), new Vector3D(1, -0.5, 2)));
        const double scale = 0.04;
        static Rect U(double x, double y, double w, double h) => new(x, y, w, h);
        SkinUv uv(Rect f, Rect b, Rect l, Rect r, Rect t, Rect d) => new(f, b, l, r, t, d);

        var head = uv(U(8, 8, 8, 8), U(24, 8, 8, 8), U(0, 8, 8, 8), U(16, 8, 8, 8), U(8, 0, 8, 8), U(16, 0, 8, 8));
        var body = uv(U(20, 20, 8, 12), U(32, 20, 8, 12), U(28, 20, 4, 12), U(16, 20, 4, 12), U(20, 16, 8, 4), U(28, 16, 8, 4));
        var rightArm = uv(U(44, 20, 4, 12), U(52, 20, 4, 12), U(40, 20, 4, 12), U(48, 20, 4, 12), U(44, 16, 4, 4), U(48, 16, 4, 4));
        var leftArm = uv(U(36, 52, 4, 12), U(44, 52, 4, 12), U(32, 52, 4, 12), U(40, 52, 4, 12), U(36, 48, 4, 4), U(40, 48, 4, 4));
        var rightLeg = uv(U(4, 20, 4, 12), U(12, 20, 4, 12), U(0, 20, 4, 12), U(8, 20, 4, 12), U(4, 16, 4, 4), U(8, 16, 4, 4));
        var leftLeg = uv(U(20, 52, 4, 12), U(28, 52, 4, 12), U(16, 52, 4, 12), U(24, 52, 4, 12), U(20, 48, 4, 4), U(24, 48, 4, 4));

        AddSkinBox(skin, bitmap, new Point3D(0, 1.04, 0), new Vector3D(8, 8, 8), head, scale);
        AddSkinBox(skin, bitmap, new Point3D(0, 0.64, 0), new Vector3D(8, 12, 4), body, scale);
        AddSkinBox(skin, bitmap, new Point3D(-0.26, 0.64, 0), new Vector3D(4, 12, 4), rightArm, scale);
        AddSkinBox(skin, bitmap, new Point3D(0.26, 0.64, 0), new Vector3D(4, 12, 4), leftArm, scale);
        AddSkinBox(skin, bitmap, new Point3D(-0.08, 0.16, 0), new Vector3D(4, 12, 4), rightLeg, scale);
        AddSkinBox(skin, bitmap, new Point3D(0.08, 0.16, 0), new Vector3D(4, 12, 4), leftLeg, scale);

        if (bitmap.PixelHeight == 64)
        {
            var headHat = uv(U(40, 8, 8, 8), U(56, 8, 8, 8), U(32, 8, 8, 8), U(48, 8, 8, 8), U(40, 0, 8, 8), U(48, 0, 8, 8));
            var bodyLayer = uv(U(20, 36, 8, 12), U(32, 36, 8, 12), U(28, 36, 4, 12), U(16, 36, 4, 12), U(20, 32, 8, 4), U(28, 32, 8, 4));
            var armLayer = uv(U(44, 36, 4, 12), U(52, 36, 4, 12), U(40, 36, 4, 12), U(48, 36, 4, 12), U(44, 32, 4, 4), U(48, 32, 4, 4));
            var legLayer = uv(U(4, 36, 4, 12), U(12, 36, 4, 12), U(0, 36, 4, 12), U(8, 36, 4, 12), U(4, 32, 4, 4), U(8, 32, 4, 4));
            AddSkinBox(skin, bitmap, new Point3D(0, 1.04, 0), new Vector3D(8.5, 8.5, 8.5), headHat, scale);
            AddSkinBox(skin, bitmap, new Point3D(0, 0.64, 0), new Vector3D(8.25, 12.25, 4.25), bodyLayer, scale);
            AddSkinBox(skin, bitmap, new Point3D(-0.26, 0.64, 0), new Vector3D(4.2, 12.2, 4.2), armLayer, scale);
            AddSkinBox(skin, bitmap, new Point3D(-0.08, 0.16, 0), new Vector3D(4.2, 12.2, 4.2), legLayer, scale);
        }

        _skinYawRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), _skinYaw);
        _skinPitchRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), _skinPitch);
        _skinTransform.Children.Clear();
        _skinTransform.Children.Add(new RotateTransform3D(_skinYawRotation, 0, 0.66, 0));
        _skinTransform.Children.Add(new RotateTransform3D(_skinPitchRotation, 0, 0.66, 0));
        ClosetSkinViewport.Children.Clear();
        ClosetSkinViewport.Children.Add(new ModelVisual3D { Content = skin, Transform = _skinTransform });
    }

    private static void AddSkinBox(Model3DGroup target, System.Windows.Media.Imaging.BitmapSource skin, Point3D center, Vector3D pixelSize, SkinUv uv, double scale)
    {
        var colorBitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(skin, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = colorBitmap.PixelWidth * 4;
        var pixels = new byte[stride * colorBitmap.PixelHeight];
        colorBitmap.CopyPixels(pixels, stride, 0);
        var hx = pixelSize.X * scale / 2; var hy = pixelSize.Y * scale / 2; var hz = pixelSize.Z * scale / 2;
        var x0 = center.X - hx; var x1 = center.X + hx; var y0 = center.Y - hy; var y1 = center.Y + hy; var z0 = center.Z - hz; var z1 = center.Z + hz;
        var p = new Dictionary<string, Point3D[]>
        {
            ["front"] = new[] { new Point3D(x0,y0,z1), new Point3D(x1,y0,z1), new Point3D(x1,y1,z1), new Point3D(x0,y1,z1) },
            ["back"] = new[] { new Point3D(x1,y0,z0), new Point3D(x0,y0,z0), new Point3D(x0,y1,z0), new Point3D(x1,y1,z0) },
            ["left"] = new[] { new Point3D(x0,y0,z0), new Point3D(x0,y0,z1), new Point3D(x0,y1,z1), new Point3D(x0,y1,z0) },
            ["right"] = new[] { new Point3D(x1,y0,z1), new Point3D(x1,y0,z0), new Point3D(x1,y1,z0), new Point3D(x1,y1,z1) },
            ["top"] = new[] { new Point3D(x0,y1,z1), new Point3D(x1,y1,z1), new Point3D(x1,y1,z0), new Point3D(x0,y1,z0) },
            ["bottom"] = new[] { new Point3D(x0,y0,z0), new Point3D(x1,y0,z0), new Point3D(x1,y0,z1), new Point3D(x0,y0,z1) }
        };
        AddFace(p["front"], uv.Front); AddFace(p["back"], uv.Back); AddFace(p["left"], uv.Left); AddFace(p["right"], uv.Right); AddFace(p["top"], uv.Top); AddFace(p["bottom"], uv.Bottom);

        void AddFace(Point3D[] points, Rect region)
        {
            if (region.X < 0 || region.Y < 0 || region.Right > skin.PixelWidth || region.Bottom > skin.PixelHeight) region = uv.Front;
            if (region.X < 0 || region.Y < 0 || region.Right > skin.PixelWidth || region.Bottom > skin.PixelHeight) return;
            var colorMeshes = new Dictionary<Color, MeshGeometry3D>();
            var pixelWidth = (int)region.Width;
            var pixelHeight = (int)region.Height;
            Point3D At(double u, double v)
            {
                var top = new Point3D(points[3].X + (points[2].X - points[3].X) * u, points[3].Y + (points[2].Y - points[3].Y) * u, points[3].Z + (points[2].Z - points[3].Z) * u);
                var bottom = new Point3D(points[0].X + (points[1].X - points[0].X) * u, points[0].Y + (points[1].Y - points[0].Y) * u, points[0].Z + (points[1].Z - points[0].Z) * u);
                return new Point3D(top.X + (bottom.X - top.X) * v, top.Y + (bottom.Y - top.Y) * v, top.Z + (bottom.Z - top.Z) * v);
            }
            for (var y = 0; y < pixelHeight; y++)
            for (var x = 0; x < pixelWidth; x++)
            {
                var offset = ((int)region.Y + y) * stride + ((int)region.X + x) * 4;
                var color = Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                if (color.A == 0) continue;
                if (!colorMeshes.TryGetValue(color, out var mesh)) colorMeshes[color] = mesh = new MeshGeometry3D();
                var u0 = (double)x / pixelWidth; var u1 = (double)(x + 1) / pixelWidth;
                var v0 = (double)y / pixelHeight; var v1 = (double)(y + 1) / pixelHeight;
                var start = mesh.Positions.Count;
                mesh.Positions.Add(At(u0, v1)); mesh.Positions.Add(At(u1, v1)); mesh.Positions.Add(At(u1, v0)); mesh.Positions.Add(At(u0, v0));
                mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
                mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 2); mesh.TriangleIndices.Add(start + 3);
            }
            foreach (var (color, mesh) in colorMeshes)
            {
                mesh.Freeze();
                var brush = new SolidColorBrush(color); brush.Freeze();
                var material = new DiffuseMaterial(brush); material.Freeze();
                target.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            }
        }
    }

    private void SkinViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _skinDragging = true; _skinDragStart = e.GetPosition(ClosetSkinViewport); ClosetSkinViewport.CaptureMouse(); e.Handled = true;
    }

    private void SkinViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_skinDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ClosetSkinViewport);
        _skinYaw += (current.X - _skinDragStart.X) * 0.8;
        _skinPitch = Math.Clamp(_skinPitch + (current.Y - _skinDragStart.Y) * 0.5, -25, 25);
        _skinDragStart = current;
        if (_skinYawRotation is not null) _skinYawRotation.Angle = _skinYaw;
        if (_skinPitchRotation is not null) _skinPitchRotation.Angle = _skinPitch;
    }

    private void SkinViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _skinDragging = false; ClosetSkinViewport.ReleaseMouseCapture();
    }

    private void SkinViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = SkinCamera.Position;
        var factor = e.Delta > 0 ? 0.9 : 1.1;
        var target = new Point3D(0, 0.66, 0);
        var direction = p - target;
        direction *= factor;
        if (direction.Length is > 1.3 and < 4.5) SkinCamera.Position = target + direction;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "Добавить профиль", Width = 430, Height = 280, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Выбери тип профиля", FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15) });
        void Option(string label, Action action)
        {
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            button.Click += (_, _) => { dialog.Close(); action(); };
            panel.Children.Add(button);
        }
        Option("Добавить профиль Ely.by", () => ElyLogin_Click(this, new RoutedEventArgs()));
        Option(L("Добавить оффлайн профиль", "Add offline profile"), () => OfflineLogin_Click(this, new RoutedEventArgs()));
        dialog.Content = panel;
        ApplyLauncherChrome(dialog, "Добавить профиль");
        dialog.ShowDialog();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not LauncherProfile { Type: "offline" } profile) { AccountStatus.Text = "Переименовывать можно только офлайн-профили."; return; }
        var input = new TextBox { Text = profile.Name };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Новое имя профиля" }); panel.Children.Add(input);
        var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new Window { Title = "Переименовать профиль", Width = 420, Height = 205, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        save.Click += (_, _) =>
        {
            var name = new string(input.Text.Where(c => char.IsLetterOrDigit(c) || c == '_').Take(16).ToArray());
            if (name.Length < 3) { ShowMessageBox(dialog, "Имя должно содержать 3–16 латинских букв, цифр или _."); return; }
            profile.Name = name; RebuildProfileChoices(); ProfileList.Items.Refresh();
            if (_activeProfile?.Id == profile.Id) { SaveProfiles(); UpdateActiveProfileUi(); }
            else SaveProfiles();
            dialog.DialogResult = true;
        };
        panel.Children.Add(save); ApplyLauncherChrome(dialog, "Переименовать профиль"); dialog.ShowDialog();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not LauncherProfile profile) return;
        if (ShowMessageBox(this, $"Удалить профиль «{profile.Name}»?", "Удалить профиль", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var oldIndex = _profiles.IndexOf(profile);
        var oldActive = _activeProfile;
        var oldActiveId = _activeProfileId;
        _profiles.Remove(profile);
        if (oldActive?.Id == profile.Id) _activeProfile = _profiles.FirstOrDefault();
        _activeProfileId = _activeProfile?.Id ?? "";
        try
        {
            SaveProfiles();
            if (profile.Type == "ely" && string.Equals(_elyAuth.Username, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                _elyAuth.AccessToken = "";
                _elyAuth.RefreshToken = "";
                _elyAuth.Username = "";
                _elyAuth.Uuid = "";
                _elyAuth.ExpiresAt = 0;
                try { SecretStore.SaveEly(JsonSerializer.Serialize(_elyAuth)); }
                catch (Exception ex)
                {
                    ShowMessageBox(this, L("Профиль удалён, но не удалось очистить токен Ely.by: ", "Profile deleted, but the Ely.by token could not be cleared: ") + ex.Message, L("Профиль удалён", "Profile deleted"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (profile.Type == "offline" && File.Exists(_profilePath))
            {
                try
                {
                    if (File.ReadAllText(_profilePath).Trim().Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                        File.Delete(_profilePath);
                }
                catch (Exception ex)
                {
                    ShowMessageBox(this, L("Профиль удалён, но не удалось очистить старую запись офлайн-профиля: ", "Profile deleted, but the legacy offline profile record could not be cleared: ") + ex.Message, L("Профиль удалён", "Profile deleted"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            RebuildProfileChoices();
            UpdateActiveProfileUi();
            AccountStatus.Text = L($"Профиль «{profile.Name}» удалён.", $"Profile ‘{profile.Name}’ deleted.");
        }
        catch (Exception ex)
        {
            _profiles.Insert(Math.Clamp(oldIndex, 0, _profiles.Count), profile);
            _activeProfile = oldActive;
            _activeProfileId = oldActiveId;
            RebuildProfileChoices();
            UpdateActiveProfileUi();
            ShowMessageBox(this, L("Не удалось удалить профиль: ", "Could not delete profile: ") + ex.Message, L("Удаление профиля", "Delete profile"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenameProfileButton.IsEnabled = ProfileList.SelectedItem is LauncherProfile { Type: "offline" };
    }
    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Tab selection can be raised by the selected TabItem itself. Ignore only nested selectors
        // (ComboBoxes and lists), otherwise the content changes while navigation colors stay stale.
        if (!ReferenceEquals(e.Source, Pages) && e.Source is not TabItem) return;
        SyncPageNavigation();
        QueuePageNavigationSync();
        if (Pages.SelectedIndex == 1) RefreshInstances();
        if (Pages.SelectedIndex == 2 && CatalogResults.Items.Count == 0) SearchCatalog_Click(this, new RoutedEventArgs());
        if (Pages.SelectedIndex == 4) RefreshFileList();
    }
    private void SyncPageNavigation()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        var selectedTab = Pages.SelectedItem as TabItem;
        var activeIndex = selectedTab is null ? 0 : Pages.Items.IndexOf(selectedTab);
        if (activeIndex < 0) activeIndex = 0;
        var header = selectedTab?.Header?.ToString() ?? "Обзор";
        TopCrumb.Text = "PistolLauncher / " + header;
        var buttons = new[] { NavHome, NavVersions, NavCatalog, NavCloset, NavFiles, NavAccounts, NavSettings };
        var tabs = Pages.Items.OfType<TabItem>().ToArray();
        for (var i = 0; i < buttons.Length; i++)
        {
            var active = i == activeIndex;
            buttons[i].Background = active ? new SolidColorBrush(Color.FromRgb(36, 41, 37)) : Brushes.Transparent;
            buttons[i].BorderBrush = active ? new SolidColorBrush(_activeAccent) : Brushes.Transparent;
            buttons[i].Foreground = active ? new SolidColorBrush(_activeAccent) : new SolidColorBrush(Color.FromRgb(238, 241, 238));
        }
        for (var i = 0; i < tabs.Length; i++)
        {
            var active = i == activeIndex;
            tabs[i].Background = active ? new SolidColorBrush(Color.FromRgb(29, 33, 30)) : Brushes.Transparent;
            tabs[i].BorderBrush = active ? new SolidColorBrush(_activeAccent) : Brushes.Transparent;
            tabs[i].Foreground = active ? new SolidColorBrush(_activeAccent) : new SolidColorBrush(Color.FromRgb(173, 180, 174));
        }
    }

    private void QueuePageNavigationSync()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(SyncPageNavigation), DispatcherPriority.ApplicationIdle);
    }

    private void UpdateNavigation() => SyncPageNavigation();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleWindowState();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ApplyLauncherChrome(Window window, string title)
    {
        var oldContent = window.Content as UIElement;
        if (_preferences.Language == "en")
        {
            title = TranslateDialogText(title);
            if (oldContent is not null) TranslateDialogTree(oldContent);
        }
        // Detach the existing visual/logical child before placing it inside the chrome grid.
        // Otherwise WPF throws "already a logical child of another element" for dialog windows.
        window.Content = null;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Grid { Background = new SolidColorBrush(Color.FromRgb(23, 25, 24)) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(228, 233, 229)) };
        titleBar.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button CaptionButton(string content, double size, RoutedEventHandler click)
        {
            var button = new Button { Content = content, Width = 46, Height = 42, Padding = new Thickness(0), FontSize = size, FontWeight = FontWeights.Normal, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            button.Click += click;
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            return button;
        }
        buttons.Children.Add(CaptionButton("—", 13, (_, _) => window.WindowState = WindowState.Minimized));
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            buttons.Children.Add(CaptionButton("□", 19, (_, _) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        var close = CaptionButton("×", 23, (_, _) => window.Close());
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(190, 45, 45));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1); titleBar.Children.Add(buttons);
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        root.Children.Add(titleBar);
        if (oldContent is not null) { Grid.SetRow(oldContent, 1); root.Children.Add(oldContent); }
        window.Content = root;
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = window.ResizeMode == ResizeMode.NoResize ? new Thickness(0) : new Thickness(7), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
    }

    private static string TranslateDialogText(string value) => value switch
    {
        "Добавить профиль" => "Add profile", "Офлайн-профиль" => "Offline profile", "Переименовать профиль" => "Rename profile",
        "Настройка Ely.by OAuth" => "Ely.by sign-in", "Вход Ely.by" => "Ely.by sign-in", "Переименовать инстанс" => "Rename instance",
        "Ошибка запуска" => "Launch error", "Новый инстанс" => "New instance", "Добавить инстанс" => "Add instance",
        "Настроить CurseForge API key" => "Configure CurseForge API key", "Minecraft не запустился" => "Minecraft failed to start",
        "Откройте папку игры" => "Open game folder", "Выбери тип профиля" => "Choose profile type", "Добавить профиль Ely.by" => "Add Ely.by profile",
        "Добавить профиль Microsoft" => "Add Microsoft profile", "Создать офлайн-профиль" => "Create offline profile", "Войти через Ely.by" => "Sign in with Ely.by",
        "Войти через Microsoft" => "Sign in with Microsoft", "Новое имя профиля" => "New profile name", "Имя локального профиля" => "Offline profile name",
        "Сохранить" => "Save", "Создать" => "Create", "Отмена" => "Cancel", "Получить ключ" => "Get key", "Понятно" => "Got it",
        "Подготовка загрузки…" => "Preparing download…", "Быстрые ответы по лаунчеру" => "Quick answers about the launcher", "Браузер" => "Browser",
        "Альфа, бета и снапшоты" => "Alpha, beta, and snapshots",
        "Новое имя инстанса" => "New instance name", "Папка инстанса тоже будет переименована." => "The instance folder will be renamed too.",
        "Открыть папку" => "Open folder", "Открыть папку инстанса" => "Open instance folder", "Переименовать" => "Rename", "Удалить" => "Delete", "Закрыть" => "Close",
        "Инстанс" => "Instance", "Инстанс с таким именем уже существует." => "An instance with this name already exists.", "В имени есть недопустимые символы." => "The name contains invalid characters.",
        "Папка не распознана как инстанс PistolLauncher." => "This folder is not recognized as a PistolLauncher instance.", "Не удалось закрыть игру" => "Could not close the game",
        "Удаление инстанса" => "Delete instance", "Удаление файла" => "Delete file", "Подтверждение удаления" => "Confirm deletion",
        "Не удалось загрузить скин" => "Could not load skin", "Не удалось удалить" => "Could not delete", "Не удалось переименовать инстанс" => "Could not rename instance",
        "Сначала выбери инстанс." => "Select an instance first.", "Выбери версию и введи название инстанса." => "Choose a version and enter an instance name.",
        "Введи название инстанса." => "Enter an instance name.", "Не удалось загрузить каталог Minecraft." => "Could not load the Minecraft version list.",
        "Вставь свой CurseForge API key. Windows сохранит его зашифрованным для твоей учётной записи, чтобы он переживал перезапуск лаунчера." => "Paste your CurseForge API key. Windows will encrypt it for your account so it remains available after restarting the launcher.",
        "Заполни оба поля." => "Fill in both fields.", "Имя должно содержать 3–16 латинских букв, цифр или _." => "The name must contain 3–16 Latin letters, digits, or underscores.",
        "Нельзя удалить этот путь из лаунчера." => "This launcher path cannot be deleted.", "Удаление отменено: папка не распознана как инстанс PistolLauncher." => "Deletion cancelled: this folder is not recognized as a PistolLauncher instance.",
        "В инстансе найдена ссылка на другой каталог. Удали её отдельно, чтобы не затронуть файлы за пределами инстанса." => "The instance contains a link to another folder. Remove it separately to avoid touching files outside the instance.",
        "Эта сборка сейчас не запущена через PistolLauncher." => "This game was not started by PistolLauncher.", "Вход Microsoft подключим следующим этапом. Выбери офлайн- или Ely.by-профиль." => "Microsoft sign-in is not available yet. Choose an offline or Ely.by profile.",
        "Название инстанса" => "Instance name", "Версия Minecraft" => "Minecraft version", "Загрузчик" => "Mod loader",
        "Создать и скачать" => "Create and download", "Новый Minecraft-инстанс" => "New Minecraft instance", "Создай чистую игру или отдельный профиль с загрузчиком." => "Create a clean game or a separate instance with a mod loader.",
        "Все загрузчики устанавливаются через официальные профили или установщики." => "All loaders are installed through their official profiles or installers.",
        "CurseForge- и Modrinth-сборки можно найти в соседних разделах." => "CurseForge and Modrinth modpacks are available in the other sections.",
        "Найди modpack и установи его отдельным инстансом." => "Find a modpack and install it as a separate instance.", "Искать" => "Search", "Установить выбранную сборку" => "Install selected modpack", _ => value
    };

    private static string TranslateDynamicDialogText(string value)
    {
        var translated = TranslateDialogText(value);
        if (translated != value) return translated;
        return value
            .Replace("Здесь регистрируется сам PistolLauncher как OAuth-клиент — это не отдельная программа. На странице Ely.by создай приложение типа «Веб-сайт» и укажи redirect URI ниже в точности, включая слеш в конце. Затем скопируй Client ID и Client secret в поля. Секрет лаунчер хранит зашифрованно для этой учётной записи Windows.", "Register PistolLauncher itself as an OAuth client; this is not a separate program. On Ely.by, create a Website application and enter the exact redirect URI shown below, including the trailing slash. Then paste the Client ID and Client secret into the fields. The launcher encrypts the secret for this Windows account.", StringComparison.Ordinal)
            .Replace("Не удалось войти", "Sign-in failed", StringComparison.Ordinal)
            .Replace("Minecraft завершился сразу после запуска (код ", "Minecraft exited immediately (code ", StringComparison.Ordinal)
            .Replace("). Лог: logs/pistol-launcher.log", "). Log: logs/pistol-launcher.log", StringComparison.Ordinal)
            .Replace("Worker вернул state, который не совпадает с текущим запросом. Проверь, что Worker передаёт полученный параметр state обратно без изменений.", "The Worker returned a state that does not match this request. Make sure it returns the received state parameter unchanged.", StringComparison.Ordinal)
            .Replace("Worker вернул code, но не access_token. Чтобы завершить вход, Worker должен обменять code и передать access_token в callback.", "The Worker returned a code but no access_token. To finish sign-in, the Worker must exchange the code and send access_token to the callback.", StringComparison.Ordinal)
            .Replace("В callback нет access_token.", "The callback did not include access_token.", StringComparison.Ordinal)
            .Replace("OAuth отклонил авторизацию: ", "OAuth sign-in was rejected: ", StringComparison.Ordinal)
            .Replace("Не удалось переименовать инстанс: ", "Could not rename instance: ", StringComparison.Ordinal)
            .Replace("Не удалось удалить инстанс: ", "Could not delete instance: ", StringComparison.Ordinal)
            .Replace("Не удалось закрыть игру: ", "Could not close the game: ", StringComparison.Ordinal)
            .Replace("Не удалось открыть скин: ", "Could not open skin: ", StringComparison.Ordinal)
            .Replace("Не удалось загрузить скин Ely.by: ", "Could not load the Ely.by skin: ", StringComparison.Ordinal);
    }

    private void TranslateDialogTree(DependencyObject node) => TranslateDialogTree(node, new HashSet<DependencyObject>());

    private void TranslateDialogTree(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return;
        if (node is TextBlock text) text.Text = TranslateDynamicDialogText(text.Text);
        if (node is ContentControl content && content.Content is string label) content.Content = TranslateDynamicDialogText(label);
        if (node is FrameworkElement element && element.ToolTip is string tip) element.ToolTip = TranslateDynamicDialogText(tip);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) TranslateDialogTree(child, visited);
        if (node is Visual or Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) TranslateDialogTree(VisualTreeHelper.GetChild(node, i), visited);
    }

    private MessageBoxResult ShowMessageBox(Window owner, string message, string caption = "PistolLauncher", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        if (IsEnglish)
        {
            message = TranslateDynamicDialogText(message);
            caption = TranslateDynamicDialogText(caption);
            message = message.Replace("Удалить инстанс «", "Delete instance ‘", StringComparison.Ordinal)
                .Replace("» вместе со всеми его файлами? Это действие нельзя отменить.", "’ and all its files? This cannot be undone.", StringComparison.Ordinal)
                .Replace("Удалить профиль «", "Delete profile ‘", StringComparison.Ordinal)
                .Replace("»?", "’?", StringComparison.Ordinal)
                .Replace("Удалить папку и всё её содержимое?", "Delete this folder and all its contents?", StringComparison.Ordinal)
                .Replace("Удалить файл?", "Delete this file?", StringComparison.Ordinal)
                .Replace("Удерживай Shift при нажатии «Удалить», чтобы пропустить это подтверждение.", "Hold Shift while clicking Delete to skip this confirmation.", StringComparison.Ordinal)
                .Replace("Не удалось сохранить ключ: ", "Could not save key: ", StringComparison.Ordinal)
                .Replace("Не удалось загрузить скин: ", "Could not load skin: ", StringComparison.Ordinal)
                .Replace("Не удалось удалить: ", "Could not delete: ", StringComparison.Ordinal)
                .Replace("Не удалось удалить инстанс: ", "Could not delete instance: ", StringComparison.Ordinal)
                .Replace("Не удалось переименовать инстанс: ", "Could not rename instance: ", StringComparison.Ordinal)
                .Replace("Не удалось закрыть игру: ", "Could not close the game: ", StringComparison.Ordinal)
                .Replace("Закрыть игру", "Close game", StringComparison.Ordinal)
                .Replace("Установщик Forge завершился с ошибкой: ", "The Forge installer failed: ", StringComparison.Ordinal)
                .Replace("Установщик NeoForge завершился с ошибкой: ", "The NeoForge installer failed: ", StringComparison.Ordinal)
                .Replace("Скин", "Skin", StringComparison.Ordinal);
        }
        var panel = new StackPanel { Margin = new Thickness(22) };
        var severityColor = icon switch
        {
            MessageBoxImage.Error => Color.FromRgb(255, 125, 105),
            MessageBoxImage.Warning => Color.FromRgb(255, 195, 90),
            MessageBoxImage.Information => _activeAccent,
            _ => _activeAccent
        };
        if (icon != MessageBoxImage.None)
        {
            var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.FromArgb(35, severityColor.R, severityColor.G, severityColor.B)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12), Child = new TextBlock { Text = icon == MessageBoxImage.Error ? "!" : icon == MessageBoxImage.Warning ? "⚠" : "i", Foreground = new SolidColorBrush(severityColor), FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            panel.Children.Add(badge);
        }
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(226, 231, 227)), MaxWidth = 520 });
        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var acceptLabel = buttons == MessageBoxButton.YesNo ? L("Да", "Yes") : L("ОК", "OK");
        var accept = new Button { Content = acceptLabel, MinWidth = 88, IsDefault = true, Background = new SolidColorBrush(_activeAccent), Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = new SolidColorBrush(_activeAccent) };
        buttonsPanel.Children.Add(accept);
        var result = MessageBoxResult.OK;
        Window? dialog = null;
        accept.Click += (_, _) => { result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK; dialog!.DialogResult = true; };
        if (buttons == MessageBoxButton.YesNo)
        {
            var cancel = new Button { Content = L("Нет", "No"), MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += (_, _) => { result = MessageBoxResult.No; dialog!.DialogResult = false; };
            buttonsPanel.Children.Insert(0, cancel);
        }
        panel.Children.Add(buttonsPanel);
        dialog = new Window { Title = caption, Owner = owner, Width = 540, MinWidth = 420, MaxHeight = 620, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = panel };
        ApplyLauncherChrome(dialog, caption);
        var confirmed = dialog.ShowDialog() == true;
        if (!confirmed && result == MessageBoxResult.OK) return MessageBoxResult.Cancel;
        return result;
    }

    private static string TranslateProgressMessage(string value) => value
        .Replace("Получаю метаданные версии", "Fetching version metadata", StringComparison.Ordinal)
        .Replace("Получаю сведения о сборке", "Fetching modpack details", StringComparison.Ordinal)
        .Replace("Распаковываю файлы сборки", "Extracting modpack files", StringComparison.Ordinal)
        .Replace("Сборка загружена; аватарку скачать не удалось", "Modpack downloaded; could not download its icon", StringComparison.Ordinal)
        .Replace("Установка завершена", "Installation complete", StringComparison.Ordinal)
        .Replace("Подготовлено файлов", "Files prepared", StringComparison.Ordinal)
        .Replace("проверяю уже загруженные", "checking existing downloads", StringComparison.Ordinal)
        .Replace("База Minecraft готова", "Minecraft base ready", StringComparison.Ordinal)
        .Replace("Скачиваю библиотеки", "Downloading libraries", StringComparison.Ordinal)
        .Replace("Загрузка файлов", "Downloading files", StringComparison.Ordinal)
        .Replace("Загрузка assets", "Downloading assets", StringComparison.Ordinal)
        .Replace(": библиотеки", ": libraries", StringComparison.Ordinal)
        .Replace("Ищу совместимый", "Finding a compatible", StringComparison.Ordinal)
        .Replace("Скачиваю", "Downloading", StringComparison.Ordinal)
        .Replace("Устанавливаю", "Installing", StringComparison.Ordinal)
        .Replace("Файлов", "files", StringComparison.Ordinal)
        .Replace("завершено", "complete", StringComparison.Ordinal)
        .Replace("загружено", "downloaded", StringComparison.Ordinal)
        .Replace("МБ", "MB", StringComparison.Ordinal);

    private async Task RunWithDownloadProgressAsync(string title, Func<IProgress<(int Percent, string Message)>, Task> operation, Window? owner = null)
    {
        var host = owner ?? this;
        var content = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.Bold, Foreground = AccentBrush, TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock { Text = "Подготовка загрузки…", Margin = new Thickness(0, 12, 0, 12), Foreground = new SolidColorBrush(Color.FromRgb(174, 182, 175)), TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12, Background = new SolidColorBrush(Color.FromRgb(48, 54, 50)), Foreground = AccentBrush };
        var percent = new TextBlock { Text = "0%", Margin = new Thickness(0, 7, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), HorizontalAlignment = HorizontalAlignment.Right };
        content.Children.Add(heading); content.Children.Add(message); content.Children.Add(bar); content.Children.Add(percent);
        var dialog = new Window { Title = title, Owner = host, Width = 510, Height = 220, MinWidth = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(16, 17, 17)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = content };
        var canClose = false;
        dialog.Closing += (_, e) => { if (!canClose) e.Cancel = true; };
        ApplyLauncherChrome(dialog, title);
        host.IsEnabled = false;
        try
        {
            dialog.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var reporter = new Progress<(int Percent, string Message)>(p =>
            {
                if (p.Percent >= 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(p.Percent, 0, 100);
                    percent.Text = $"{Math.Clamp(p.Percent, 0, 100)}%";
                }
                else { bar.IsIndeterminate = true; percent.Text = ""; }
                message.Text = IsEnglish ? TranslateProgressMessage(p.Message) : p.Message;
                DownloadProgress.Value = p.Percent < 0 ? DownloadProgress.Value : Math.Clamp(p.Percent, 0, 100);
                DetailText.Text = p.Message;
            });
            await operation(reporter);
        }
        finally
        {
            canClose = true;
            dialog.Close();
            host.IsEnabled = true;
            host.Activate();
        }
    }

    private static async Task<string> DownloadFileWithProgressAsync(string url, string destination, IProgress<(int Percent, string Message)> progress, int start, int span, string label)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Загрузка разрешена только по HTTPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var finalDestination = File.Exists(destination) ? FindAvailableDownloadPath(destination) : destination;
        FileStream? output = null;
        var completed = false;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync();
            for (var attempt = 0; output is null; attempt++)
            {
                try { output = new FileStream(finalDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true); }
                catch (IOException) when (File.Exists(finalDestination)) { finalDestination = FindAvailableDownloadPath(destination); }
                catch (IOException) when (attempt < 5) { await Task.Delay(150 * (attempt + 1)); }
            }
            await using (output)
            {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total > 0)
                {
                    var fraction = Math.Clamp((double)received / total, 0, 1);
                    progress.Report((start + (int)(span * fraction), $"{label}: {received / 1048576.0:0.0} / {total / 1048576.0:0.0} МБ"));
                }
                else progress.Report((-1, $"{label}: загружено {received / 1048576.0:0.0} МБ"));
            }
            }
            completed = true;
            progress.Report((start + span, $"{label}: завершено"));
            return finalDestination;
        }
        finally
        {
            output?.Dispose();
            if (!completed) try { if (File.Exists(finalDestination)) File.Delete(finalDestination); } catch (IOException) { }
        }
    }

    private static string FindAvailableDownloadPath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void Assistant_Click(object sender, RoutedEventArgs e)
    {
        var english = IsEnglish;
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = "Pistol FAQ", FontSize = 23, FontWeight = FontWeights.Bold, Foreground = AccentBrush, Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(new TextBlock { Text = english ? "Quick help for PistolLauncher" : "Краткая справка по PistolLauncher", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), Margin = new Thickness(0, 0, 0, 15) });
        if (english)
        {
            AddFaq("How do I install Minecraft?", "Open Instances → Add instance, enter a name, then choose a Minecraft version and loader. Each instance is installed separately under minecraft/instances.");
            AddFaq("How do I launch an instance?", "Select it in the Instances tab and press Play. Java is downloaded automatically when needed; you can also set a Java folder in Settings.");
            AddFaq("What can I find in Catalog?", "Search for mods, resource packs, shaders, and modpacks on Modrinth or CurseForge. Choose an instance before downloading. CurseForge searches require your API key.");
            AddFaq("What does Shared folder mean?", "For resource packs and shaders, choose Shared folder in the target list to sync them to every instance while Minecraft is running. Choosing an instance installs them only there.");
            AddFaq("How do I sign in with Ely.by?", "Add an Ely.by profile from Accounts. The browser opens for authorization; after it returns to PistolLauncher, the profile is saved automatically.");
            AddFaq("How do I change an offline skin?", "Select an offline profile, open Wardrobe, and press Upload skin. Use a Minecraft skin PNG sized 64×64 or 64×32; its preview updates immediately.");
            AddFaq("Where are launcher settings?", "Settings lets you change the interface language and accent color, Minecraft and Java folders, and the RAM allocation used when launching the game.");
            AddFaq("Where are my Minecraft files?", "The Minecraft tab browses the configured Minecraft data folder. Double-click a folder to open it; file deletion asks for confirmation unless Shift is held.");
            AddFaq("Does Microsoft sign-in work?", "Microsoft sign-in is not connected yet. Ely.by and offline profiles are available.");
        }
        else
        {
            AddFaq("Как установить Minecraft?", "Открой «Версии» → «Добавить инстанс», введи имя и выбери версию Minecraft и загрузчик. Каждый инстанс устанавливается отдельно в minecraft/instances.");
            AddFaq("Как запустить инстанс?", "Выбери его во вкладке «Версии» и нажми «Играть». При необходимости Java скачается автоматически; также можно указать её папку в настройках.");
            AddFaq("Что можно найти в каталоге?", "Ищи моды, ресурспаки, шейдеры и сборки на Modrinth и CurseForge. Перед скачиванием выбери инстанс. Для поиска на CurseForge нужен API-ключ.");
            AddFaq("Что значит «Общая папка»?", "Для ресурспаков и шейдеров выбери «Общая папка» в списке цели, чтобы синхронизировать их со всеми инстансами на время игры. Выбор инстанса установит их только туда.");
            AddFaq("Как войти через Ely.by?", "Добавь профиль Ely.by во вкладке «Аккаунты». Откроется браузер для авторизации; после возврата в PistolLauncher профиль сохранится автоматически.");
            AddFaq("Как поменять офлайн-скин?", "Выбери офлайн-профиль, открой «Шкафчик» и нажми «Загрузить скин». Нужен PNG 64×64 или 64×32; предпросмотр обновится сразу.");
            AddFaq("Где настройки лаунчера?", "В настройках можно сменить язык и цвет темы, пути Minecraft и Java, а также объём оперативной памяти для запуска игры.");
            AddFaq("Где файлы Minecraft?", "Вкладка «Minecraft» показывает выбранную папку данных. Двойной щелчок открывает папку; удаление файла требует подтверждения, если не зажат Shift.");
            AddFaq("Работает ли вход Microsoft?", "Авторизация Microsoft пока не подключена. Уже доступны Ely.by и офлайн-профили.");
        }
        void AddFaq(string question, string answer)
        {
            content.Children.Add(new TextBlock { Text = question, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 3) });
            content.Children.Add(new TextBlock { Text = answer, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(174, 182, 175)) });
        }
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var faq = new Window { Title = "Pistol FAQ", Owner = this, Width = 560, Height = 620, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(16, 17, 17)), Foreground = Brushes.White, Content = scroll };
        ApplyLauncherChrome(faq, "Pistol FAQ");
        faq.ShowDialog();
    }

    private async void SearchCatalog_Click(object sender, RoutedEventArgs e) => await SearchCatalogAsync();

    private async void CatalogFilters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, TargetInstance) && TargetInstance.SelectedItem is CatalogTargetOption { Instance: { } selectedInstance })
        {
            _lastCatalogInstance = selectedInstance;
            _selectedInstancePath = selectedInstance.Path;
            if (!ReferenceEquals(InstanceList.SelectedItem, selectedInstance)) InstanceList.SelectedItem = selectedInstance;
        }
        if (!_catalogHasSearched || !IsLoaded || Pages.SelectedIndex != 2) return;
        _catalogFilterCts?.Cancel();
        _catalogFilterCts?.Dispose();
        var debounce = _catalogFilterCts = new CancellationTokenSource();
        try { await Task.Delay(250, debounce.Token); await SearchCatalogAsync(); }
        catch (OperationCanceledException) { }
    }

    private async Task SearchCatalogAsync()
    {
        var searchGeneration = Interlocked.Increment(ref _catalogSearchGeneration);
        _catalogHasSearched = true;
        _catalogItems.Clear();
        _catalogLastSourceErrors.Clear();
        _catalogLoadingMore = false;
        _catalogModrinthOffset = _catalogCurseForgeOffset = 0;
        _catalogMoreModrinth = _catalogMoreCurseForge = false;
        _catalogPageQuery = CatalogQuery.Text.Trim();
        _catalogPageVersion = SelectedGameVersion;
        _catalogPageLoader = SelectedCatalogLoader;
        var selectedType = (ProjectKind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mod";
        if (SelectedCatalogInstance is null && !IsCatalogSharedTarget)
        {
            CatalogStatus.Text = L("Сначала создай инстанс во вкладке «Версии», затем выбери его здесь.", "Create an instance in the Instances tab, then select it here.");
            return;
        }
        if (IsCatalogSharedTarget && selectedType == "mod")
        {
            CatalogStatus.Text = L("«Общая папка» доступна для ресурспаков и шейдеров. Для модов выбери инстанс.", "Shared folder is for resource packs and shaders. Select an instance for mods.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedGameVersion))
        {
            CatalogStatus.Text = L("У выбранного инстанса не указана версия Minecraft. Выбери другой инстанс.", "The selected instance has no Minecraft version. Choose another instance.");
            return;
        }
        var source = (CatalogSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var type = (ProjectKind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mod";
        _catalogPageType = type;
        CatalogStatus.Text = L("Ищу проекты…", "Searching projects…");
        try
        {
            var selectedLoader = SelectedCatalogLoader;
            var results = new List<ModProject>();
            var sourceErrors = new List<string>();
            if (source is "all" or "modrinth")
            {
                try { var found = await SearchModrinthAsync(_catalogPageQuery, type, SelectedGameVersion, selectedLoader); results.AddRange(found); _catalogModrinthOffset = found.Count; _catalogMoreModrinth = found.Count >= 50; }
                catch (Exception ex) { sourceErrors.Add("Modrinth: " + ex.Message); }
            }
            if (source is "all" or "curseforge")
            {
                if (string.IsNullOrWhiteSpace(_curseForgeKey))
                {
                    if (source == "curseforge") throw new InvalidOperationException("CurseForge требует API key. Нажми кнопку «Настроить CurseForge API key» ниже.");
                    sourceErrors.Add("CurseForge пропущен: добавь API key кнопкой ниже.");
                }
                else
                {
                    try { var found = await SearchCurseForgeAsync(_catalogPageQuery, type); results.AddRange(found); _catalogCurseForgeOffset = found.Count; _catalogMoreCurseForge = found.Count >= 50; }
                    catch (Exception ex) { sourceErrors.Add("CurseForge: " + ex.Message); }
                }
            }
            if (searchGeneration != _catalogSearchGeneration) return;
            var orderedResults = results.OrderByDescending(p => p.Downloads).ToList();
            foreach (var project in orderedResults) _catalogItems.Add(project);
            foreach (var project in orderedResults) _ = CacheProjectAvatarAsync(project);
            _catalogLastSourceErrors.AddRange(sourceErrors);
            CatalogStatus.Text = FormatCatalogResultStatus(results.Count, _catalogLastSourceErrors);
        }
        catch (Exception ex) { if (searchGeneration == _catalogSearchGeneration) CatalogStatus.Text = L("Ошибка каталога: ", "Catalog error: ") + ex.Message; }
    }

    private async void CatalogResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 3) return;
        await LoadMoreCatalogAsync();
    }

    private async Task LoadMoreCatalogAsync()
    {
        if (_catalogLoadingMore || (!_catalogMoreModrinth && !_catalogMoreCurseForge)) return;
        var generation = _catalogSearchGeneration;
        _catalogLoadingMore = true;
        try
        {
            var added = new List<ModProject>();
            if (_catalogMoreModrinth)
            {
                try
                {
                    var page = await SearchModrinthAsync(_catalogPageQuery, _catalogPageType, _catalogPageVersion, _catalogPageLoader, _catalogModrinthOffset);
                    if (generation != _catalogSearchGeneration) return;
                    _catalogModrinthOffset += page.Count;
                    _catalogMoreModrinth = page.Count >= 50;
                    added.AddRange(page);
                }
                catch { _catalogMoreModrinth = false; }
            }
            if (_catalogMoreCurseForge)
            {
                try
                {
                    var page = await SearchCurseForgeAsync(_catalogPageQuery, _catalogPageType, _catalogCurseForgeOffset);
                    if (generation != _catalogSearchGeneration) return;
                    _catalogCurseForgeOffset += page.Count;
                    _catalogMoreCurseForge = page.Count >= 50;
                    added.AddRange(page);
                }
                catch { _catalogMoreCurseForge = false; }
            }
            var existing = _catalogItems.Select(p => p.Source + ":" + p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var project in added.OrderByDescending(p => p.Downloads))
                if (existing.Add(project.Source + ":" + project.Id)) { _catalogItems.Add(project); _ = CacheProjectAvatarAsync(project); }
            if (added.Count == 0) CatalogStatus.Text += L(" · Больше результатов нет.", " · No more results.");
        }
        finally { if (generation == _catalogSearchGeneration) _catalogLoadingMore = false; }
    }

    private async Task CacheProjectAvatarAsync(ModProject project)
    {
        if (string.IsNullOrWhiteSpace(project.IconUrl)) return;
        var directory = Path.Combine(_launcherRoot, ".assets", "cache", "avatars");
        var source = string.Concat(project.Source.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        var kind = string.Concat(project.Type.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        var id = string.Concat(project.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var cachePath = Path.Combine(directory, $"{source}_{kind}_{id}.png");
        try
        {
            if (!File.Exists(cachePath))
            {
                await AvatarDownloadGate.WaitAsync();
                try
                {
                    if (!File.Exists(cachePath))
                    {
                        var bytes = await Http.GetByteArrayAsync(project.IconUrl);
                        Directory.CreateDirectory(directory);
                        var temporary = cachePath + ".part";
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, bytes);
                            SaveAsPng(temporary, cachePath + ".write");
                            File.Move(cachePath + ".write", cachePath, overwrite: true);
                        }
                        finally { try { File.Delete(temporary); } catch { } try { File.Delete(cachePath + ".write"); } catch { } }
                    }
                }
                finally { AvatarDownloadGate.Release(); }
            }
            project.Avatar = LoadBitmap(cachePath);
        }
        catch { /* Broken/missing CDN avatars should not break catalog results. */ }
    }

    private static ImageSource LoadBitmap(string path)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        return image;
    }

    private static void SaveAsPng(string sourcePath, string destinationPath)
    {
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(sourcePath); bitmap.EndInit();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(destinationPath);
        encoder.Save(output);
    }

    private static async Task<List<ModProject>> SearchModrinthAsync(string query, string type, string gameVersion, string? loader = null, int offset = 0)
    {
        var facetGroups = new List<string[]> { new[] { "project_type:" + type } };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facetGroups.Add(new[] { "versions:" + gameVersion });
        if (type == "mod" && !string.IsNullOrWhiteSpace(loader) && loader != "vanilla") facetGroups.Add(new[] { "categories:" + loader });
        var facets = Uri.EscapeDataString(JsonSerializer.Serialize(facetGroups));
        var effectiveQuery = string.IsNullOrWhiteSpace(query) ? "minecraft" : query;
        var uri = "https://api.modrinth.com/v2/search?limit=50&offset=" + Math.Max(0, offset) + "&index=" + (string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance") + "&facets=" + facets + "&query=" + Uri.EscapeDataString(effectiveQuery);
        using var response = await Http.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("hits").EnumerateArray().Select(hit => new ModProject
        {
            Id = hit.GetProperty("project_id").GetString() ?? "",
            Title = hit.GetProperty("title").GetString() ?? "(без названия)",
            Description = hit.GetProperty("description").GetString() ?? "",
            Type = hit.GetProperty("project_type").GetString() ?? type,
            Source = "Modrinth",
            IconUrl = hit.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() ?? "" : "",
            Downloads = hit.TryGetProperty("downloads", out var n) ? n.GetInt64() : 0
        }).ToList();
    }

    private async Task<List<ModProject>> SearchCurseForgeAsync(string query, string type, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(_curseForgeKey)) throw new InvalidOperationException("Введи свой CurseForge API key кнопкой внизу страницы.");
        var classId = type switch { "modpack" => 4471, "resourcepack" => 12, "shader" => 6552, _ => 6 };
        var parameters = new List<string> { "gameId=432", "classId=" + classId, "pageSize=50", "index=" + Math.Max(0, offset), "sortField=2", "sortOrder=desc" };
        if (type is "mod" or "resourcepack" or "shader")
        {
            var version = SelectedGameVersion;
            if (!string.IsNullOrWhiteSpace(version)) parameters.Add("gameVersion=" + Uri.EscapeDataString(version));
        }
        if (type == "mod")
        {
            var loaderId = SelectedCatalogLoader switch { "forge" => 1, "fabric" => 4, "neoforge" => 6, "quilt" => 5, _ => 0 };
            if (loaderId != 0 && !string.IsNullOrWhiteSpace(SelectedGameVersion)) parameters.Add("modLoaderType=" + loaderId);
        }
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add("searchFilter=" + Uri.EscapeDataString(query.Trim()));
        var uri = "https://api.curseforge.com/v1/mods/search?" + string.Join("&", parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-api-key", _curseForgeKey);
        using var response = await Http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseBody}");
        using var json = JsonDocument.Parse(responseBody);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(hit => new ModProject
        {
            Id = hit.GetProperty("id").GetInt32().ToString(),
            Title = hit.GetProperty("name").GetString() ?? "(без названия)",
            Description = hit.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "",
            Type = type,
            Source = "CurseForge",
            IconUrl = hit.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object && logo.TryGetProperty("thumbnailUrl", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.String ? thumbnail.GetString() ?? "" : "",
            Downloads = hit.TryGetProperty("downloadCount", out var n) ? n.GetInt64() : 0
        }).ToList();
    }

    private async void DownloadProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModProject project }) return;
        if (project.Type == "modpack")
        {
            await InstallCatalogPackAsync(project, CatalogStatus);
            return;
        }
        try
        {
            CatalogStatus.Text = $"Получаю совместимую версию {project.Title}…";
            var loader = SelectedCatalogLoader;
            string downloadUrl, fileName;
            string? expectedHash = null;
            var expectedMd5 = false;
            if (project.Source == "Modrinth")
            {
                var gv = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { SelectedGameVersion }));
                var versionUrl = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(project.Id)}/version?game_versions={gv}";
                if (project.Type == "mod" && !string.IsNullOrWhiteSpace(loader)) versionUrl += "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }));
                using var response = await Http.GetAsync(versionUrl);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var file = json.RootElement.EnumerateArray().SelectMany(v => v.GetProperty("files").EnumerateArray()).FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
                if (file.ValueKind == JsonValueKind.Undefined) file = json.RootElement.EnumerateArray().SelectMany(v => v.GetProperty("files").EnumerateArray()).FirstOrDefault();
                if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Нет релиза для этой версии Minecraft и загрузчика.");
                downloadUrl = file.GetProperty("url").GetString()!;
                fileName = file.GetProperty("filename").GetString()!;
                if (file.TryGetProperty("hashes", out var hashes))
                {
                    if (hashes.TryGetProperty("sha1", out var sha1)) expectedHash = sha1.GetString();
                    else if (hashes.TryGetProperty("sha512", out var sha512)) expectedHash = sha512.GetString();
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_curseForgeKey)) throw new InvalidOperationException("Нужен CurseForge API key.");
                var queryParts = new List<string> { "pageSize=50", "index=0" };
                if ((project.Type is "mod" or "resourcepack" or "shader") && !string.IsNullOrWhiteSpace(SelectedGameVersion)) queryParts.Add("gameVersion=" + Uri.EscapeDataString(SelectedGameVersion));
                if (project.Type == "mod")
                {
                    var loaderId = SelectedCatalogLoader switch { "forge" => 1, "fabric" => 4, "neoforge" => 6, "quilt" => 5, _ => 0 };
                    if (loaderId != 0 && !string.IsNullOrWhiteSpace(SelectedGameVersion)) queryParts.Add("modLoaderType=" + loaderId);
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.curseforge.com/v1/mods/{project.Id}/files?" + string.Join("&", queryParts));
                request.Headers.Add("x-api-key", _curseForgeKey);
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var file = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(item => item.TryGetProperty("isAvailable", out var available) && available.GetBoolean() && item.TryGetProperty("downloadUrl", out var url) && !string.IsNullOrWhiteSpace(url.GetString()));
                if (file.ValueKind == JsonValueKind.Undefined || !file.TryGetProperty("downloadUrl", out var url) || string.IsNullOrWhiteSpace(url.GetString())) throw new InvalidOperationException("У CurseForge нет доступной прямой загрузки этого файла.");
                downloadUrl = url.GetString()!;
                fileName = file.GetProperty("fileName").GetString()!;
                if (file.TryGetProperty("hashes", out var hashes))
                    foreach (var hash in hashes.EnumerateArray())
                    {
                        var algorithm = hash.TryGetProperty("algo", out var algo) ? algo.GetInt32() : 0;
                        if (algorithm is 1 or 2) { expectedHash = hash.GetProperty("value").GetString(); expectedMd5 = algorithm == 2; break; }
                    }
            }
            if (project.Type is "mod" or "resourcepack" or "shader" && SelectedCatalogInstance is null && !IsCatalogSharedTarget) throw new InvalidOperationException("Сначала выбери инстанс или общую папку.");
            if (project.Type == "mod" && IsCatalogSharedTarget) throw new InvalidOperationException("Для модов выбери конкретный инстанс.");
            var destinationDir = project.Type switch
            {
                "modpack" => Path.Combine(_root, "downloads"),
                "resourcepack" => IsCatalogSharedTarget ? Path.Combine(_root, "resourcepacks") : Path.Combine(SelectedCatalogInstance!.Path, "resourcepacks"),
                "shader" => IsCatalogSharedTarget ? Path.Combine(_root, "shaderpacks") : Path.Combine(SelectedCatalogInstance!.Path, "shaderpacks"),
                _ => Path.Combine(SelectedCatalogInstance!.Path, "mods")
            };
            Directory.CreateDirectory(destinationDir);
            var archivePath = Path.Combine(destinationDir, Path.GetFileName(fileName));
            if (File.Exists(archivePath) && !string.IsNullOrWhiteSpace(expectedHash))
            {
                try
                {
                    var alreadyDownloaded = expectedMd5 ? await VerifyMd5Async(archivePath, expectedHash) : await VerifyHashAsync(archivePath, expectedHash, CancellationToken.None);
                    if (alreadyDownloaded)
                    {
                        CatalogStatus.Text = $"Этот файл уже есть: {Path.GetRelativePath(AppContext.BaseDirectory, archivePath)}";
                        return;
                    }
                }
                catch (IOException) { }
            }
            await RunWithDownloadProgressAsync($"Скачивание {project.Title}", async progress =>
            {
                archivePath = await DownloadFileWithProgressAsync(downloadUrl, archivePath, progress, 0, 100, project.Title);
            }, Window.GetWindow((DependencyObject)sender));
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var verified = expectedMd5 ? await VerifyMd5Async(archivePath, expectedHash) : await VerifyHashAsync(archivePath, expectedHash, CancellationToken.None);
                if (!verified) { File.Delete(archivePath); throw new InvalidDataException("Хеш файла не совпал; загрузка отклонена."); }
            }
            if (project.Type == "modpack" && project.Source == "Modrinth")
            {
                var instancePath = await InstallMrPackAsync(archivePath);
                CatalogStatus.Text = "Сборка установлена: " + Path.GetRelativePath(AppContext.BaseDirectory, instancePath);
            }
            else if (project.Type == "modpack" && project.Source == "CurseForge")
            {
                var (instancePath, skipped) = await InstallCurseForgePackAsync(archivePath);
                CatalogStatus.Text = skipped.Count == 0
                    ? "Сборка установлена: " + Path.GetRelativePath(AppContext.BaseDirectory, instancePath)
                    : $"Сборка частично установлена: {skipped.Count} файлов недоступны через API. Подробности в INCOMPLETE.txt внутри инстанса.";
            }
            else CatalogStatus.Text = project.Type == "modpack" ? "Архив сборки скачан. Автоимпорт CurseForge добавим после настройки ключа и проверки формата." : $"Скачано в выбранный инстанс: {Path.GetRelativePath(AppContext.BaseDirectory, archivePath)}";
        }
        catch (Exception ex) { CatalogStatus.Text = "Не удалось скачать: " + ex.Message; }
    }

    private async Task InstallCatalogPackAsync(ModProject project, TextBlock status)
    {
        var host = Window.GetWindow(status) ?? this;
        await RunWithDownloadProgressAsync($"Установка сборки: {project.Title}", progress => InstallCatalogPackCoreAsync(project, status, progress), host);
    }

    private async Task InstallCatalogPackCoreAsync(ModProject project, TextBlock status, IProgress<(int Percent, string Message)> progress)
    {
        try
        {
            status.Text = $"Ищу файл сборки «{project.Title}»…";
            progress.Report((0, "Получаю сведения о сборке…"));
            string downloadUrl;
            string fileName;
            string? expectedHash = null;
            var expectedMd5 = false;
            if (project.Source == "Modrinth")
            {
                using var response = await Http.GetAsync($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(project.Id)}/version");
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var candidates = json.RootElement.EnumerateArray().SelectMany(v => v.GetProperty("files").EnumerateArray()).ToArray();
                var file = candidates.FirstOrDefault(f => f.TryGetProperty("filename", out var name) && name.GetString()!.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) && f.TryGetProperty("primary", out var primary) && primary.GetBoolean());
                if (file.ValueKind == JsonValueKind.Undefined) file = candidates.FirstOrDefault(f => f.TryGetProperty("filename", out var name) && name.GetString()!.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
                if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Modrinth не вернул файл .mrpack для этой сборки.");
                downloadUrl = file.GetProperty("url").GetString()!;
                fileName = file.GetProperty("filename").GetString()!;
                if (file.TryGetProperty("hashes", out var hashes))
                {
                    if (hashes.TryGetProperty("sha1", out var sha1)) expectedHash = sha1.GetString();
                    else if (hashes.TryGetProperty("sha512", out var sha512)) expectedHash = sha512.GetString();
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_curseForgeKey)) throw new InvalidOperationException("Сначала добавь CurseForge API key.");
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(project.Id)}/files?pageSize=50&index=0");
                request.Headers.Add("x-api-key", _curseForgeKey);
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var file = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(f => f.TryGetProperty("isAvailable", out var available) && available.GetBoolean() && f.TryGetProperty("downloadUrl", out var url) && !string.IsNullOrWhiteSpace(url.GetString()));
                if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("CurseForge не предоставил прямую ссылку на файл этой сборки.");
                downloadUrl = file.GetProperty("downloadUrl").GetString()!;
                fileName = file.GetProperty("fileName").GetString()!;
                if (file.TryGetProperty("hashes", out var hashes))
                    foreach (var hash in hashes.EnumerateArray())
                    {
                        var algorithm = hash.TryGetProperty("algo", out var algo) ? algo.GetInt32() : 0;
                        if (algorithm is 1 or 2) { expectedHash = hash.GetProperty("value").GetString(); expectedMd5 = algorithm == 2; break; }
                    }
            }
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Ссылка на сборку должна использовать HTTPS.");
            Directory.CreateDirectory(Path.Combine(_root, "downloads"));
            var archivePath = Path.Combine(_root, "downloads", Path.GetFileName(fileName));
            status.Text = $"Скачиваю «{project.Title}»…";
            archivePath = await DownloadFileWithProgressAsync(uri.AbsoluteUri, archivePath, progress, 2, 18, $"Сборка {project.Title}");
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var verified = expectedMd5 ? await VerifyMd5Async(archivePath, expectedHash) : await VerifyHashAsync(archivePath, expectedHash, CancellationToken.None);
                if (!verified) throw new InvalidDataException("Хеш архива сборки не совпал; архив нельзя устанавливать.");
            }
            string instancePath;
            List<string> skipped = new();
            progress.Report((20, "Распаковываю файлы сборки…"));
            if (project.Source == "Modrinth") instancePath = await InstallMrPackAsync(archivePath, progress);
            else (instancePath, skipped) = await InstallCurseForgePackAsync(archivePath, progress);
            if (!string.IsNullOrWhiteSpace(project.IconUrl))
            {
                try { _ = await DownloadFileWithProgressAsync(project.IconUrl, Path.Combine(instancePath, "icon.png"), progress, 58, 4, "Аватарка сборки"); }
                catch (Exception ex) { progress.Report((62, "Сборка загружена; аватарку скачать не удалось: " + ex.Message)); }
            }
            var (gameVersion, loader, packLoaderVersion) = ReadPackRuntime(archivePath, project.Source);
            var runtimeReady = await PrepareImportedPackAsync(instancePath, gameVersion, loader, packLoaderVersion, status, progress);
            RefreshInstances();
            var note = skipped.Count > 0 ? $" · пропущено файлов CurseForge: {skipped.Count}" : "";
            status.Text = runtimeReady
                ? $"Сборка установлена и готова к запуску: {Path.GetFileName(instancePath)}{note}"
                : $"Файлы сборки импортированы, но загрузчик {loader} ещё не устанавливается автоматически.{note}";
            progress.Report((100, "Установка завершена"));
        }
        catch (Exception ex) { status.Text = "Не удалось установить сборку: " + ex.Message; }
    }

    private static (string GameVersion, string Loader, string LoaderVersion) ReadPackRuntime(string archivePath, string source)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestPath = source == "Modrinth" ? "modrinth.index.json" : "manifest.json";
        var entry = archive.GetEntry(manifestPath) ?? throw new InvalidDataException($"В архиве нет {manifestPath}.");
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (source == "Modrinth")
        {
            if (!root.TryGetProperty("dependencies", out var deps) || !deps.TryGetProperty("minecraft", out var game)) throw new InvalidDataException("В modrinth.index.json не указана версия Minecraft.");
            foreach (var loaderName in new[] { "fabric-loader", "quilt-loader", "forge", "neoforge" })
                if (deps.TryGetProperty(loaderName, out var loader)) return (game.GetString() ?? "", loaderName switch { "fabric-loader" => "Fabric", "quilt-loader" => "Quilt", "neoforge" => "NeoForge", _ => "Forge" }, loader.GetString() ?? "");
            return (game.GetString() ?? "", "Vanilla", "");
        }
        var minecraft = root.GetProperty("minecraft");
        var gameVersion = minecraft.GetProperty("version").GetString() ?? "";
        var loaderId = minecraft.TryGetProperty("modLoaders", out var modLoaders) && modLoaders.GetArrayLength() > 0 ? modLoaders[0].GetProperty("id").GetString() ?? "" : "vanilla";
        var kind = loaderId.Split('-', 2)[0].ToLowerInvariant();
        var normalizedLoaderName = kind switch { "fabric" => "Fabric", "quilt" => "Quilt", "neoforge" => "NeoForge", "forge" => "Forge", _ => "Vanilla" };
        var loaderVersion = loaderId.Contains('-') ? loaderId[(loaderId.IndexOf('-') + 1)..] : "";
        return (gameVersion, normalizedLoaderName, loaderVersion);
    }

    private async Task<bool> PrepareImportedPackAsync(string instancePath, string gameVersion, string loader, string declaredLoaderVersion, TextBlock status, IProgress<(int Percent, string Message)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) throw new InvalidDataException("В сборке не указана версия Minecraft.");
        if (_versions.Count == 0) await LoadVersionsAsync();
        var version = _versions.FirstOrDefault(v => v.Id == gameVersion);
        if (version is null) throw new InvalidDataException($"Версия Minecraft {gameVersion} отсутствует в официальном каталоге.");
        status.Text = $"Подготавливаю Minecraft {gameVersion} для сборки…";
        var runtimeProgress = progress is null
            ? new Progress<(int Percent, string Message)>(p => status.Text = p.Message)
            : new Progress<(int Percent, string Message)>(p => { status.Text = p.Message; progress.Report((62 + (int)(p.Percent * 0.37), p.Message)); });
        await InstallVersionAsync(version, instancePath, runtimeProgress, CancellationToken.None, loader is "Fabric" or "Quilt");
        var loaderVersion = declaredLoaderVersion;
        if (loader is "Fabric" or "Quilt" or "Forge" or "NeoForge") loaderVersion = await InstallLoaderProfileAsync(gameVersion, loader, instancePath, runtimeProgress, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(instancePath, ".pistol-instance.json"), JsonSerializer.Serialize(new { name = Path.GetFileName(instancePath), gameVersion, loader, loaderVersion }));
        return loader is "Vanilla" or "Fabric" or "Quilt" or "Forge" or "NeoForge";
    }

    private async Task<string> InstallMrPackAsync(string archivePath, IProgress<(int Percent, string Message)>? progress = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var indexEntry = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("В архиве нет modrinth.index.json.");
        byte[] manifestBytes;
        await using (var manifestSource = indexEntry.Open()) { using var buffer = new MemoryStream(); await manifestSource.CopyToAsync(buffer); manifestBytes = buffer.ToArray(); }
        using var manifest = JsonDocument.Parse(manifestBytes);
        var doc = manifest.RootElement;
        var name = doc.TryGetProperty("name", out var title) ? title.GetString() ?? "Modpack" : "Modpack";
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "Modpack";
        var instanceDir = Path.Combine(_root, "instances", slug);
        for (var suffix = 2; Directory.Exists(instanceDir); suffix++) instanceDir = Path.Combine(_root, "instances", slug + "-" + suffix);
        Directory.CreateDirectory(instanceDir);

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')))
        {
            var rel = entry.FullName["overrides/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var target = SafeChildPath(instanceDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open(); await using var dest = File.Create(target); await source.CopyToAsync(dest);
        }

        var packFiles = doc.GetProperty("files").EnumerateArray().ToArray();
        int done = 0;
        foreach (var item in packFiles)
        {
            var clientEnv = item.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) ? client.GetString() : null;
            if (clientEnv == "unsupported") { done++; continue; }
            if (!item.TryGetProperty("downloads", out var urls) || urls.GetArrayLength() == 0) { done++; continue; }
            var target = SafeChildPath(instanceDir, item.GetProperty("path").GetString() ?? throw new InvalidDataException("Пустой путь файла в пакете."));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string? expectedHash = null;
            if (item.TryGetProperty("hashes", out var hashes))
            {
                if (hashes.TryGetProperty("sha1", out var sha1)) expectedHash = sha1.GetString();
                else if (hashes.TryGetProperty("sha512", out var sha512)) expectedHash = sha512.GetString();
            }
            if (File.Exists(target) && expectedHash is not null && await VerifyHashAsync(target, expectedHash, ct: default)) { done++; continue; }
            var itemStart = 20 + (int)(38.0 * done / Math.Max(1, packFiles.Length));
            var itemSpan = Math.Max(1, (int)Math.Ceiling(38.0 / Math.Max(1, packFiles.Length)));
            target = await DownloadFileWithProgressAsync(urls[0].GetString()!, target, progress ?? new Progress<(int Percent, string Message)>(_ => { }), itemStart, itemSpan, Path.GetFileName(target));
            if (expectedHash is not null && !await VerifyHashAsync(target, expectedHash, default)) { File.Delete(target); throw new InvalidDataException("SHA-сумма файла сборки не совпала: " + Path.GetFileName(target)); }
            done++;
            progress?.Report((20 + (int)(38.0 * done / Math.Max(1, packFiles.Length)), $"Устанавливаю файлы Modrinth: {done} / {packFiles.Length}"));
        }
        await File.WriteAllBytesAsync(Path.Combine(instanceDir, "modrinth.index.json"), manifestBytes);
        return instanceDir;
    }

    private async Task<(string InstancePath, List<string> Skipped)> InstallCurseForgePackAsync(string archivePath, IProgress<(int Percent, string Message)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(_curseForgeKey)) throw new InvalidOperationException("Нужен CurseForge API key.");
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("В ZIP нет manifest.json CurseForge.");
        byte[] manifestBytes;
        await using (var manifestSource = manifestEntry.Open()) { using var buffer = new MemoryStream(); await manifestSource.CopyToAsync(buffer); manifestBytes = buffer.ToArray(); }
        using var manifest = JsonDocument.Parse(manifestBytes);
        var doc = manifest.RootElement;
        var name = doc.TryGetProperty("name", out var title) ? title.GetString() ?? "CurseForge-Pack" : "CurseForge-Pack";
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "CurseForge-Pack";
        var instanceDir = Path.Combine(_root, "instances", slug);
        for (var suffix = 2; Directory.Exists(instanceDir); suffix++) instanceDir = Path.Combine(_root, "instances", slug + "-" + suffix);
        Directory.CreateDirectory(instanceDir);

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')))
        {
            var rel = entry.FullName["overrides/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var target = SafeChildPath(instanceDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open(); await using var dest = File.Create(target); await source.CopyToAsync(dest);
        }

        var skipped = new List<string>();
        var packFiles = doc.GetProperty("files").EnumerateArray().ToArray();
        int done = 0;
        foreach (var item in packFiles)
        {
            if (item.TryGetProperty("required", out var required) && !required.GetBoolean()) { done++; continue; }
            var projectId = item.GetProperty("projectID").GetInt32();
            var fileId = item.GetProperty("fileID").GetInt32();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}");
            request.Headers.Add("x-api-key", _curseForgeKey);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { skipped.Add($"{projectId}/{fileId}: API HTTP {(int)response.StatusCode}"); done++; continue; }
            using var fileInfo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var file = fileInfo.RootElement.GetProperty("data");
            if (!file.TryGetProperty("isAvailable", out var available) || !available.GetBoolean() || !file.TryGetProperty("downloadUrl", out var urlElement) || string.IsNullOrWhiteSpace(urlElement.GetString()))
            { skipped.Add($"{projectId}/{fileId}: unavailable/direct download not provided"); done++; continue; }
            var fileName = file.GetProperty("fileName").GetString() ?? $"{projectId}-{fileId}.jar";
            var targetPath = SafeChildPath(instanceDir, "mods/" + Path.GetFileName(fileName));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var itemStart = 20 + (int)(38.0 * done / Math.Max(1, packFiles.Length));
            var itemSpan = Math.Max(1, (int)Math.Ceiling(38.0 / Math.Max(1, packFiles.Length)));
            targetPath = await DownloadFileWithProgressAsync(urlElement.GetString()!, targetPath, progress ?? new Progress<(int Percent, string Message)>(_ => { }), itemStart, itemSpan, fileName);
            if (file.TryGetProperty("hashes", out var hashes))
            {
                foreach (var hash in hashes.EnumerateArray())
                {
                    var expected = hash.GetProperty("value").GetString();
                    var algo = hash.GetProperty("algo").GetInt32();
                    if (expected is null) continue;
                    var valid = algo == 1 ? await VerifyHashAsync(targetPath, expected, default) : algo == 2 ? await VerifyMd5Async(targetPath, expected) : true;
                    if (!valid) { File.Delete(targetPath); throw new InvalidDataException("Хеш файла CurseForge не совпал: " + fileName); }
                }
            }
            done++;
            progress?.Report((20 + (int)(38.0 * done / Math.Max(1, packFiles.Length)), $"Устанавливаю файлы CurseForge: {done} / {packFiles.Length}"));
        }
        await File.WriteAllBytesAsync(Path.Combine(instanceDir, "manifest.json"), manifestBytes);
        if (skipped.Count > 0) await File.WriteAllLinesAsync(Path.Combine(instanceDir, "INCOMPLETE.txt"), skipped);
        return (instanceDir, skipped);
    }

    private static string SafeChildPath(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(p => p == "..")) throw new InvalidDataException("В архиве обнаружен недопустимый путь.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь файла выходит за пределы инстанса.");
        return fullPath;
    }

    private static async Task<bool> VerifyHashAsync(string path, string hash, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var actual = hash.Length == 128 ? Convert.ToHexString(await SHA512.HashDataAsync(stream, ct)).ToLowerInvariant() : Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
        return actual.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> VerifyMd5Async(string path, string hash)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant();
        return actual.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureCurseForge_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "CurseForge API key", Width = 560, Height = 260, MinWidth = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Вставь свой CurseForge API key. Windows сохранит его зашифрованным для твоей учётной записи, чтобы он переживал перезапуск лаунчера.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MinHeight = 42 });
        var input = new PasswordBox { Password = _curseForgeKey ?? "", Background = new SolidColorBrush(Color.FromRgb(17, 20, 18)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(57, 65, 59)), Padding = new Thickness(10, 8, 10, 8), MinHeight = 38 };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var getKey = new Button { Content = "Получить ключ", Margin = new Thickness(0, 0, 8, 0) };
        getKey.Click += (_, _) => OpenCurseForgeKeyPage_Click(this, new RoutedEventArgs());
        var save = new Button { Content = "Сохранить" };
        save.Click += (_, _) =>
        {
            try
            {
                SecretStore.Save(input.Password);
                _curseForgeKey = input.Password.Trim();
                CatalogStatus.Text = string.IsNullOrEmpty(_curseForgeKey) ? "CurseForge API key удалён." : "CurseForge API key сохранён в зашифрованном виде.";
                dialog.DialogResult = true;
            }
            catch (Exception ex) { ShowMessageBox(dialog, "Не удалось сохранить ключ: " + ex.Message); }
        };
        buttons.Children.Add(getKey); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = panel; ApplyLauncherChrome(dialog, "CurseForge API key"); dialog.ShowDialog();
    }

    private void OpenCurseForgeKeyPage_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key?locale=en") { UseShellExecute = true });

    private void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { Text = "Player" };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Имя локального профиля" }); panel.Children.Add(input);
        var save = new Button { Content = "Создать", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window { Title = "Офлайн-профиль", Width = 400, Height = 210, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        save.Click += (_, _) =>
        {
            var name = new string(input.Text.Where(c => char.IsLetterOrDigit(c) || c == '_').Take(16).ToArray());
            if (name.Length < 3) { ShowMessageBox(dialog, "Имя должно содержать 3–16 латинских букв, цифр или _."); return; }
            var profile = new LauncherProfile { Name = name, Type = "offline" };
            _profiles.Add(profile); _activeProfile = profile; _activeProfileId = profile.Id;
            SaveProfiles(); RebuildProfileChoices(); UpdateActiveProfileUi();
            dialog.DialogResult = true;
        };
        panel.Children.Add(save); ApplyLauncherChrome(dialog, "Офлайн-профиль"); dialog.ShowDialog();
    }

    private void MicrosoftLogin_Click(object sender, RoutedEventArgs e) => ShowMessageBox(this, "Экран выбора Microsoft готов. Следующий этап — подключить OAuth и проверку владения Minecraft.", "Microsoft вход", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void ElyLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AccountStatus.Text = "Ожидаю авторизацию в браузере…";
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, 25565);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var authorize = new UriBuilder(ElyWorkerLoginUrl)
            {
                Query = "state=" + Uri.EscapeDataString(state)
            };
            Process.Start(new ProcessStartInfo(authorize.Uri.ToString()) { UseShellExecute = true });
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            listener.Stop();
            BringLauncherToFront();
            var callbackRequest = await ReadCallbackRequestAsync(client.GetStream(), timeout.Token);
            var requestParts = callbackRequest.RequestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2 || !requestParts[1].StartsWith("/callback", StringComparison.Ordinal)) throw new InvalidDataException("OAuth вернул запрос на неожиданный callback.");
            var callback = new Uri("http://127.0.0.1" + requestParts[1]);
            var query = ParseQuery(callback.Query);
            foreach (var pair in ParseCallbackBody(callbackRequest.Headers, callbackRequest.Body)) query[pair.Key] = pair.Value;
            var responseBody = "Вход завершён. Можно вернуться в PistolLauncher.";
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>PistolLauncher</title><style>body{margin:0;background:#101311;color:#e8eee9;font:16px Segoe UI,Arial;display:grid;min-height:100vh;place-items:center}.card{box-sizing:border-box;width:min(560px,calc(100% - 36px));padding:36px;border:1px solid #343c36;border-radius:18px;background:#191e1a;box-shadow:0 24px 80px #0008}.mark{color:#bdff4a;font-size:13px;letter-spacing:.14em;text-transform:uppercase}h1{font-size:28px;margin:14px 0 8px}p{color:#aab7ae;line-height:1.6;margin:0}</style><main class=card><div class=mark>PistolLauncher</div><h1>Авторизация завершена</h1><p>" + responseBody + "</p></main></html>");
            var headerBytes = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await client.GetStream().WriteAsync(headerBytes, timeout.Token);
            await client.GetStream().WriteAsync(bodyBytes, timeout.Token);
            if (query.TryGetValue("error", out var oauthError)) throw new InvalidOperationException("OAuth отклонил авторизацию: " + oauthError);
            // The custom Worker currently returns access_token, username and uuid, without state.
            // Validate state when the Worker supplies it, while keeping this documented contract usable.
            if (query.TryGetValue("state", out var returnedState) && !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(returnedState)))
                throw new InvalidOperationException("Worker вернул state, который не совпадает с текущим запросом. Проверь, что Worker передаёт полученный параметр state обратно без изменений.");
            if (!query.TryGetValue("access_token", out var accessToken) && !query.TryGetValue("token", out accessToken))
                throw new InvalidDataException(query.ContainsKey("code")
                    ? "Worker вернул code, но не access_token. Чтобы завершить вход, Worker должен обменять code и передать access_token в callback."
                    : "В callback нет access_token.");
            _elyAuth.AccessToken = accessToken;
            _elyAuth.RefreshToken = query.TryGetValue("refresh_token", out var refresh) ? refresh : "";
            _elyAuth.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (query.TryGetValue("expires_in", out var expiresText) && long.TryParse(expiresText, out var seconds) ? seconds : 86400);
            _elyAuth.Username = query.TryGetValue("username", out var returnedUsername) ? returnedUsername : "";
            _elyAuth.Uuid = query.TryGetValue("uuid", out var returnedUuid) ? returnedUuid : "";
            if (string.IsNullOrWhiteSpace(_elyAuth.Username) || string.IsNullOrWhiteSpace(_elyAuth.Uuid))
            {
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://account.ely.by/api/account/v1/info");
            profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _elyAuth.AccessToken);
            using var profileResponse = await Http.SendAsync(profileRequest);
            profileResponse.EnsureSuccessStatusCode();
            using var profileDoc = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
            var profile = profileDoc.RootElement;
                if (string.IsNullOrWhiteSpace(_elyAuth.Username)) _elyAuth.Username = profile.GetProperty("username").GetString() ?? "Ely.by";
                if (string.IsNullOrWhiteSpace(_elyAuth.Uuid)) _elyAuth.Uuid = profile.GetProperty("uuid").GetString() ?? "";
            }
            SecretStore.SaveEly(JsonSerializer.Serialize(_elyAuth));
            var elyProfile = _profiles.FirstOrDefault(p => p.Type == "ely" && p.Name.Equals(_elyAuth.Username, StringComparison.OrdinalIgnoreCase));
            if (elyProfile is null) { elyProfile = new LauncherProfile { Name = _elyAuth.Username, Type = "ely" }; _profiles.Add(elyProfile); }
            _activeProfile = elyProfile; _activeProfileId = elyProfile.Id;
            SaveProfiles(); RebuildProfileChoices(); UpdateActiveProfileUi();
        }
        catch (Exception ex)
        {
            AccountStatus.Text = "Не удалось войти через Ely.by";
            ShowOAuthError(ex.Message);
        }
    }

    private void ShowOAuthError(string message)
    {
        var panel = new StackPanel { Margin = new Thickness(26) };
        var badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = new SolidColorBrush(Color.FromRgb(55, 37, 34)), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = "!", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(255, 125, 105)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        panel.Children.Add(badge);
        panel.Children.Add(new TextBlock { Text = "Не удалось войти", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8), Foreground = Brushes.White });
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(172, 184, 175)), LineHeight = 21 });
        var close = new Button { Content = "Понятно", Width = 112, Height = 40, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0), IsDefault = true };
        panel.Children.Add(close);
        var dialog = new Window { Title = "Вход Ely.by", Owner = this, Width = 500, Height = 300, MinWidth = 440, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize, Content = panel };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => Close();
        ApplyLauncherChrome(dialog, "Вход Ely.by");
        dialog.ShowDialog();
    }

    private void BringLauncherToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private bool ConfigureElyOAuth()
    {
        Process.Start(new ProcessStartInfo(ElyWorkerLoginUrl) { UseShellExecute = true });
        var dialog = new Window { Title = "Настройка Ely.by OAuth", Width = 560, Height = 390, MinWidth = 540, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Здесь регистрируется сам PistolLauncher как OAuth-клиент — это не отдельная программа. На странице Ely.by создай приложение типа «Веб-сайт» и укажи redirect URI ниже в точности, включая слеш в конце. Затем скопируй Client ID и Client secret в поля. Секрет лаунчер хранит зашифрованно для этой учётной записи Windows.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var redirect = new TextBox { Text = ElyRedirectUri, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "Redirect URI" }); panel.Children.Add(redirect);
        var clientId = new TextBox { Text = _elyAuth.ClientId, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "Client ID" }); panel.Children.Add(clientId);
        var clientSecret = new PasswordBox { Password = _elyAuth.ClientSecret, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "Client secret" }); panel.Children.Add(clientSecret);
        var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(clientId.Text) || string.IsNullOrWhiteSpace(clientSecret.Password)) { ShowMessageBox(dialog, "Заполни оба поля."); return; }
            _elyAuth = new ElyOAuthData { ClientId = clientId.Text.Trim(), ClientSecret = clientSecret.Password.Trim() };
            SecretStore.SaveEly(JsonSerializer.Serialize(_elyAuth));
            dialog.DialogResult = true;
        };
        panel.Children.Add(save); dialog.Content = panel;
        ApplyLauncherChrome(dialog, "Настройка Ely.by OAuth");
        return dialog.ShowDialog() == true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    private static async Task<(string RequestLine, Dictionary<string, string> Headers, string Body)> ReadCallbackRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(1024);
        var one = new byte[1];
        while (headerBytes.Count < 32 * 1024)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0) throw new EndOfStreamException("OAuth callback оборвал соединение до передачи заголовков.");
            headerBytes.Add(one[0]);
            var count = headerBytes.Count;
            if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
        }
        if (headerBytes.Count >= 32 * 1024) throw new InvalidDataException("Заголовки OAuth callback слишком большие.");
        var headerText = System.Text.Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("OAuth callback не содержит HTTP-запроса.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        var contentLength = headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var parsedLength) ? parsedLength : 0;
        if (contentLength < 0 || contentLength > 1024 * 1024) throw new InvalidDataException("OAuth callback body имеет недопустимый размер.");
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("OAuth callback оборвал соединение при передаче данных.");
            offset += read;
        }
        return (lines[0], headers, System.Text.Encoding.UTF8.GetString(body));
    }

    private static Dictionary<string, string> ParseCallbackBody(Dictionary<string, string> headers, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers.TryGetValue("Content-Type", out var contentType) && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OAuth callback JSON должен быть объектом.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    values[property.Name] = property.Value.ToString();
            }
            return values;
        }
        return ParseQuery(body);
    }

    private async Task RefreshElyTokenIfNeededAsync()
    {
        if (string.IsNullOrWhiteSpace(_elyAuth.AccessToken) || string.IsNullOrWhiteSpace(_elyAuth.Uuid))
            throw new InvalidOperationException("Профиль Ely.by не авторизован. Войди через Ely.by ещё раз.");
        if (_elyAuth.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return;
        if (string.IsNullOrWhiteSpace(_elyAuth.RefreshToken)) throw new InvalidOperationException("Срок токена Ely.by истёк. Войди через Ely.by ещё раз.");
        using var response = await Http.PostAsync("https://account.ely.by/api/oauth2/v1/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _elyAuth.ClientId, ["client_secret"] = _elyAuth.ClientSecret,
            ["scope"] = "account_info minecraft_server_session offline_access",
            ["refresh_token"] = _elyAuth.RefreshToken, ["grant_type"] = "refresh_token"
        }));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement;
        _elyAuth.AccessToken = token.GetProperty("access_token").GetString() ?? "";
        _elyAuth.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (token.TryGetProperty("expires_in", out var expires) ? expires.GetInt64() : 86400);
        SecretStore.SaveEly(JsonSerializer.Serialize(_elyAuth));
    }

    private void MinecraftProfile_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.minecraft.net/msaprofile/mygames/editskin") { UseShellExecute = true });
    private async void ChooseSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is not { Type: "offline" } profile) return;
        var picker = new OpenFileDialog { Filter = L("PNG-изображения (*.png)|*.png", "PNG images (*.png)|*.png"), Title = L("Выбери PNG-скин", "Choose a PNG skin") };
        if (picker.ShowDialog(this) != true) return;
        var destination = Path.Combine(_launcherRoot, ".assets", "skins", profile.Id + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            var image = LoadBitmap(picker.FileName) as System.Windows.Media.Imaging.BitmapSource ?? throw new InvalidDataException("Файл не является изображением.");
            if (image.PixelWidth != 64 || image.PixelHeight is not (64 or 32)) throw new InvalidDataException("Нужна стандартная текстура скина Minecraft 64×64 или 64×32 пикселя.");
            SaveAsPng(picker.FileName, destination);
        }
        catch (Exception ex) { ShowMessageBox(this, "Не удалось загрузить скин: " + ex.Message, "Скин", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        profile.SkinPath = destination;
        SaveProfiles();
        ClosetStatus.Text = _preferences.Language == "en" ? "Updating skin preview…" : "Обновляю предпросмотр скина…";
        await LoadSelectedSkinAsync(profile);
    }

    private void RefreshFileList()
    {
        Directory.CreateDirectory(_currentFileDir);
        FilePath.Text = Path.GetRelativePath(AppContext.BaseDirectory, _currentFileDir);
        FileList.Items.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_currentFileDir).OrderBy(Path.GetFileName)) FileList.Items.Add("📁  " + Path.GetFileName(dir) + "\t" + dir);
            foreach (var file in Directory.EnumerateFiles(_currentFileDir).OrderBy(Path.GetFileName)) FileList.Items.Add("▤  " + Path.GetFileName(file) + "\t" + file);
        }
        catch (Exception ex) { FileList.Items.Add("Ошибка чтения: " + ex.Message); }
    }
    private void FilesBack_Click(object sender, RoutedEventArgs e) { if (_fileHistory.Count > 0) { _currentFileDir = _fileHistory.Pop(); RefreshFileList(); } }
    private void FilesUp_Click(object sender, RoutedEventArgs e) { if (!string.Equals(_currentFileDir, _root, StringComparison.OrdinalIgnoreCase)) { var parent = Directory.GetParent(_currentFileDir)?.FullName; if (parent is not null && IsInsideMinecraft(parent)) { _fileHistory.Push(_currentFileDir); _currentFileDir = parent; RefreshFileList(); } } }
    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not string item || item.Contains("Ошибка чтения:")) return;
        var separator = item.IndexOf('\t'); if (separator < 0) return;
        var path = item[(separator + 1)..];
        if (Directory.Exists(path) && IsInsideMinecraft(path)) { _fileHistory.Push(_currentFileDir); _currentFileDir = path; RefreshFileList(); }
        else if (File.Exists(path) && IsInsideMinecraft(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        DeleteSelectedMinecraftEntry(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e) => DeleteSelectedMinecraftEntry(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    private void DeleteSelectedMinecraftEntry(bool skipConfirmation)
    {
        if (FileList.SelectedItem is not string item || item.Contains("Ошибка чтения:")) return;
        var separator = item.IndexOf('\t');
        if (separator < 0) return;
        var path = Path.GetFullPath(item[(separator + 1)..]);
        if (!IsInsideMinecraft(path) || path.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            ShowMessageBox(this, "Нельзя удалить этот путь из лаунчера.", "Удаление файла", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) { RefreshFileList(); return; }
        if (!skipConfirmation)
        {
            var text = isDirectory
                ? $"Удалить папку и всё её содержимое?\n\n{path}\n\nУдерживай Shift при нажатии «Удалить», чтобы пропустить это подтверждение."
                : $"Удалить файл?\n\n{path}\n\nУдерживай Shift при нажатии «Удалить», чтобы пропустить это подтверждение.";
            if (ShowMessageBox(this, text, "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        try
        {
            if (isDirectory) Directory.Delete(path, recursive: true); else File.Delete(path);
            RefreshFileList();
        }
        catch (Exception ex) { ShowMessageBox(this, "Не удалось удалить: " + ex.Message, "Удаление файла", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private bool IsInsideMinecraft(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshInstances_Click(object sender, RoutedEventArgs e) => RefreshInstances();

    private void MigrateLegacyVersions()
    {
        var legacyRoot = Path.Combine(_root, "versions");
        if (!Directory.Exists(legacyRoot)) return;
        foreach (var oldDir in Directory.EnumerateDirectories(legacyRoot))
        {
            var version = Path.GetFileName(oldDir);
            if (!File.Exists(Path.Combine(oldDir, ".installed"))) continue;
            var name = "Minecraft " + version;
            var instanceDir = Path.Combine(_root, "instances", name);
            for (var suffix = 2; Directory.Exists(instanceDir); suffix++) instanceDir = Path.Combine(_root, "instances", name + " " + suffix);
            try
            {
                var targetVersions = Path.Combine(instanceDir, "versions");
                Directory.CreateDirectory(targetVersions);
                Directory.Move(oldDir, Path.Combine(targetVersions, version));
                File.WriteAllText(Path.Combine(instanceDir, ".pistol-instance.json"), JsonSerializer.Serialize(new { name, gameVersion = version, loader = "Vanilla" }));
            }
            catch { }
        }
    }

    private string EnsureInstanceIdentity(string instanceDirectory)
    {
        var markerPath = Path.Combine(instanceDirectory, ".pistol-instance.json");
        JsonObject metadata;
        try { metadata = File.Exists(markerPath) ? JsonNode.Parse(File.ReadAllText(markerPath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { metadata = new JsonObject(); }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instancesRoot = Path.Combine(_root, "instances");
        if (Directory.Exists(instancesRoot))
            foreach (var other in Directory.EnumerateDirectories(instancesRoot))
            {
                if (Path.GetFullPath(other).Equals(Path.GetFullPath(instanceDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var otherMarker = Path.Combine(other, ".pistol-instance.json");
                    if (!File.Exists(otherMarker)) continue;
                    var otherId = JsonNode.Parse(File.ReadAllText(otherMarker))?["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(otherId)) usedIds.Add(otherId);
                }
                catch { }
            }

        string? id = null;
        try { id = metadata["id"]?.GetValue<string>(); } catch { }
        if (!Guid.TryParseExact(id, "N", out _) || usedIds.Contains(id!))
        {
            var avatarRootForCollision = Path.Combine(_launcherRoot, ".assets", "avatars");
            do { id = Guid.NewGuid().ToString("N"); } while (usedIds.Contains(id) || File.Exists(Path.Combine(avatarRootForCollision, id + ".png")));
            metadata["id"] = id;
        }
        metadata["id"] = id;
        metadata["name"] ??= Path.GetFileName(instanceDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var avatarDirectory = Path.Combine(_launcherRoot, ".assets", "avatars");
        Directory.CreateDirectory(avatarDirectory);
        var avatarPath = Path.Combine(avatarDirectory, id + ".png");
        if (!File.Exists(avatarPath))
        {
            var packIcon = Path.Combine(instanceDirectory, "icon.png");
            var defaultIcon = Path.Combine(avatarDirectory, "default.png");
            try
            {
                if (File.Exists(packIcon)) SaveAsPng(packIcon, avatarPath);
                else if (File.Exists(defaultIcon)) File.Copy(defaultIcon, avatarPath, overwrite: false);
            }
            catch
            {
                try { if (File.Exists(avatarPath)) File.Delete(avatarPath); } catch { }
                if (File.Exists(defaultIcon) && !File.Exists(avatarPath)) File.Copy(defaultIcon, avatarPath, overwrite: false);
            }
        }
        return id!;
    }

    private void RefreshInstances()
    {
        Directory.CreateDirectory(Path.Combine(_root, "instances"));
        var selectedTarget = TargetInstance.SelectedItem as CatalogTargetOption;
        var wasSharedTarget = selectedTarget?.IsShared == true;
        var preferredPath = _selectedInstancePath ?? selectedTarget?.Instance?.Path;
        _instances.Clear();
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(_root, "instances")).OrderBy(Path.GetFileName))
        {
            var markerPath = Path.Combine(dir, ".pistol-instance.json");
            var mrpackPath = Path.Combine(dir, "modrinth.index.json");
            var cursePath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(markerPath) && !File.Exists(mrpackPath) && !File.Exists(cursePath)) continue;
            var name = Path.GetFileName(dir);
            var gameVersion = ""; var loader = "Vanilla";
            try
            {
                var metaPath = File.Exists(markerPath) ? markerPath : File.Exists(mrpackPath) ? mrpackPath : cursePath;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(metaPath));
                var json = doc.RootElement;
                if (File.Exists(markerPath))
                {
                    if (json.TryGetProperty("name", out var n)) name = n.GetString() ?? name;
                    if (json.TryGetProperty("gameVersion", out var v)) gameVersion = v.GetString() ?? "";
                    if (json.TryGetProperty("loader", out var l)) loader = l.GetString() ?? "Vanilla";
                    if (json.TryGetProperty("loaderVersion", out var lv) && !string.IsNullOrWhiteSpace(lv.GetString())) loader += " " + lv.GetString();
                }
                else if (File.Exists(mrpackPath))
                {
                    name = json.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
                    if (json.TryGetProperty("dependencies", out var deps))
                    {
                        if (deps.TryGetProperty("minecraft", out var v)) gameVersion = v.GetString() ?? "";
                        foreach (var candidate in new[] { "fabric-loader", "forge", "neoforge", "quilt-loader" }) if (deps.TryGetProperty(candidate, out var l)) { loader = candidate.Replace("-loader", "") + " " + l.GetString(); break; }
                    }
                }
                else if (json.TryGetProperty("minecraft", out var mc))
                {
                    gameVersion = mc.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.GetArrayLength() > 0) loader = loaders[0].GetProperty("id").GetString() ?? "Vanilla";
                }
            }
            catch { }
            var instanceId = EnsureInstanceIdentity(dir);
            ImageSource? avatar = null;
            var avatarPath = Path.Combine(_launcherRoot, ".assets", "avatars", instanceId + ".png");
            if (File.Exists(avatarPath)) try { avatar = LoadBitmap(avatarPath); } catch { }
            _instances.Add(new GameInstance { Id = instanceId, Name = name, Path = dir, GameVersion = gameVersion, Loader = loader, Avatar = avatar });
        }
        HomeInstanceSummary.Text = _instances.Count == 0
            ? L("Пока нет инстансов — создай первый.", "No instances yet — create your first.")
            : L($"Инстансов: {_instances.Count}", $"Instances: {_instances.Count}");
        var selected = _instances.FirstOrDefault(i => preferredPath is not null && i.Path.Equals(preferredPath, StringComparison.OrdinalIgnoreCase)) ?? _instances.FirstOrDefault();
        _catalogTargets.Clear();
        foreach (var instance in _instances) _catalogTargets.Add(new CatalogTargetOption { Name = instance.Name, Instance = instance });
        _catalogTargets.Add(new CatalogTargetOption { Name = L("Общая папка", "Shared folder"), IsShared = true });
        _lastCatalogInstance = selected;
        TargetInstance.SelectedItem = wasSharedTarget ? _catalogTargets.LastOrDefault(o => o.IsShared) : _catalogTargets.FirstOrDefault(o => o.Instance == selected);
        InstanceList.SelectedItem = selected;
        _selectedInstancePath = selected?.Path;
    }

    private void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstanceList.SelectedItem is GameInstance instance)
        {
            _selectedInstancePath = instance.Path;
            _lastCatalogInstance = instance;
            var option = _catalogTargets.FirstOrDefault(item => item.Instance == instance);
            if (option is not null && !ReferenceEquals(TargetInstance.SelectedItem, option)) TargetInstance.SelectedItem = option;
        }
    }

    private void OpenInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameInstance instance }) Process.Start(new ProcessStartInfo(instance.Path) { UseShellExecute = true });
    }

    private void RenameInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameInstance instance }) return;
        var field = new TextBox { Text = instance.Name, MinHeight = 36, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 14) };
        var save = new Button { Content = "Переименовать", IsDefault = true, Background = AccentBrush, Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = AccentBrush, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Новое имя инстанса", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = AccentBrush });
        panel.Children.Add(new TextBlock { Text = "Папка инстанса тоже будет переименована.", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), Margin = new Thickness(0, 5, 0, 0) });
        panel.Children.Add(field); panel.Children.Add(save);
        var dialog = new Window { Title = "Переименовать инстанс", Owner = this, Width = 470, Height = 220, MinWidth = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(16, 17, 17)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize, Content = panel };
        save.Click += (_, _) => dialog.DialogResult = true;
        ApplyLauncherChrome(dialog, "Переименовать инстанс");
        if (dialog.ShowDialog() != true) return;
        var newName = field.Text.Trim().TrimEnd('.', ' ');
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName is "." or "..")
        {
            ShowMessageBox(this, "В имени есть недопустимые символы.", "Переименовать инстанс", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldPath = Path.GetFullPath(instance.Path);
        var instancesRoot = Path.GetFullPath(Path.Combine(_root, "instances")) + Path.DirectorySeparatorChar;
        if (!oldPath.StartsWith(instancesRoot, StringComparison.OrdinalIgnoreCase))
        {
            ShowMessageBox(this, "Папка не распознана как инстанс PistolLauncher.", "Переименовать инстанс", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(newPath))
        {
            ShowMessageBox(this, "Инстанс с таким именем уже существует.", "Переименовать инстанс", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var markerPath = Path.Combine(oldPath, ".pistol-instance.json");
            var originalMarker = File.Exists(markerPath) ? File.ReadAllText(markerPath) : null;
            var marker = originalMarker is null ? new JsonObject() : JsonNode.Parse(originalMarker) as JsonObject ?? new JsonObject();
            marker["name"] = newName;
            if (marker["gameVersion"] is null) marker["gameVersion"] = instance.GameVersion;
            if (marker["loader"] is null) marker["loader"] = instance.Loader.Split(' ', 2)[0];
            File.WriteAllText(markerPath, marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Move(oldPath, newPath); }
            catch
            {
                if (originalMarker is null) File.Delete(markerPath); else File.WriteAllText(markerPath, originalMarker);
                throw;
            }
            if (_selectedInstancePath is not null && Path.GetFullPath(_selectedInstancePath).Equals(oldPath, StringComparison.OrdinalIgnoreCase)) _selectedInstancePath = newPath;
            RefreshInstances();
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Path.Equals(newPath, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = $"Инстанс переименован в «{newName}»";
        }
        catch (Exception ex) { ShowMessageBox(this, "Не удалось переименовать инстанс: " + ex.Message, "Переименовать инстанс", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void CloseInstanceGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameInstance instance }) return;
        if (!_runningInstances.TryGetValue(Path.GetFullPath(instance.Path), out var process) || process.HasExited)
        {
            ShowMessageBox(this, "Эта сборка сейчас не запущена через PistolLauncher.", "PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            process.CloseMainWindow();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            StatusText.Text = $"Игра для «{instance.Name}» закрыта";
            DetailText.Text = "Процесс Minecraft завершён.";
        }
        catch (Exception ex)
        {
            ShowMessageBox(this, "Не удалось закрыть игру: " + ex.Message, "PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameInstance instance }) return;
        var instancesRoot = Path.GetFullPath(Path.Combine(_root, "instances"));
        var target = Path.GetFullPath(instance.Path);
        var expectedPrefix = instancesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !_instances.Any(item => string.Equals(Path.GetFullPath(item.Path), target, StringComparison.OrdinalIgnoreCase)))
        {
            ShowMessageBox(this, "Удаление отменено: папка не распознана как инстанс PistolLauncher.", "PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var directory = new DirectoryInfo(target);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || ContainsReparsePoint(target))
        {
            ShowMessageBox(this, "В инстансе найдена ссылка на другой каталог. Удали её отдельно, чтобы не затронуть файлы за пределами инстанса.", "PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ShowMessageBox(this, $"Удалить инстанс «{instance.Name}» вместе со всеми его файлами? Это действие нельзя отменить.",
            "Удаление инстанса", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Directory.Delete(target, recursive: true);
            if (string.Equals(_selectedInstancePath, target, StringComparison.OrdinalIgnoreCase)) _selectedInstancePath = null;
            RefreshInstances();
            StatusText.Text = $"Инстанс «{instance.Name}» удалён";
            DetailText.Text = "Файлы инстанса удалены; общие библиотеки и assets оставлены для остальных версий.";
        }
        catch (Exception ex)
        {
            ShowMessageBox(this, "Не удалось удалить инстанс: " + ex.Message, "PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }

    private void HomeInstance_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HomeInstanceList.SelectedItem is GameInstance instance)
        {
            Pages.SelectedIndex = 1;
            InstanceList.SelectedItem = instance;
        }
    }

    private async void LaunchInstance_Click(object sender, RoutedEventArgs e)
    {
        var instance = Pages.SelectedIndex == 0
            ? HomeInstanceList.SelectedItem as GameInstance
            : InstanceList.SelectedItem as GameInstance;
        if (instance is null) { ShowMessageBox(this, "Сначала выбери инстанс.", "PistolLauncher"); return; }
        if (_activeProfile is null) { ShowMessageBox(this, L("Сначала добавь или выбери профиль.", "Add or select a profile first."), "PistolLauncher"); return; }
        if (_activeProfile?.Type == "microsoft") { ShowMessageBox(this, "Вход Microsoft подключим следующим этапом. Выбери офлайн- или Ely.by-профиль.", "Microsoft профиль"); return; }
        try
        {
            StatusText.Text = $"Подготавливаю {instance.Name} к запуску…";
            var sharedLedger = Path.Combine(instance.Path, ".pistol-shared-content.json");
            var hasSharedContent = File.Exists(sharedLedger) || Directory.Exists(Path.Combine(_root, "resourcepacks")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "resourcepacks")).Any() || Directory.Exists(Path.Combine(_root, "shaderpacks")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "shaderpacks")).Any();
            if (hasSharedContent)
                await RunWithDownloadProgressAsync("Синхронизация общих паков", progress => SyncSharedPacksAsync(instance.Path, progress));
            var process = await StartInstanceAsync(instance);
            await Task.Delay(1200);
            if (process.HasExited)
            {
                var logPath = Path.Combine(instance.Path, "logs", "pistol-launcher.log");
                StatusText.Text = $"Minecraft не запустился (код {process.ExitCode})";
                DetailText.Text = "Открой лог запуска в папке инстанса.";
                ShowLaunchFailure(instance, process.ExitCode, logPath);
                return;
            }
            StatusText.Text = $"Minecraft запущен: {instance.Name}";
            DetailText.Text = "Игра работает. Чтобы закрыть её, нажми «Закрыть» у этого инстанса.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось запустить Minecraft";
            DetailText.Text = ex.GetType().Name + ": " + ex.Message;
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "launch.log"), $"[{DateTimeOffset.Now:O}] {ex}\r\n\r\n");
            }
            catch { }
            ShowMessageBox(this, ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SyncSharedPacksAsync(string instancePath, IProgress<(int Percent, string Message)> progress)
    {
        var ledgerPath = Path.Combine(instancePath, ".pistol-shared-content.json");
        Dictionary<string, string> ledger;
        try { ledger = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(ledgerPath)) ?? new(StringComparer.OrdinalIgnoreCase); }
        catch { ledger = new(StringComparer.OrdinalIgnoreCase); }
        var sharedItems = new List<(string Key, string Source)>();
        foreach (var category in new[] { "resourcepacks", "shaderpacks" })
        {
            var directory = Path.Combine(_root, category);
            if (!Directory.Exists(directory)) continue;
            foreach (var source in Directory.EnumerateFileSystemEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                sharedItems.Add((category + "/" + Path.GetFileName(source), source));
            }
        }
        var liveKeys = sharedItems.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = Math.Max(1, sharedItems.Count + ledger.Keys.Count(key => !liveKeys.Contains(key)));
        var completed = 0;

        foreach (var item in sharedItems)
        {
            var target = SafeChildPath(instancePath, item.Key);
            var sourceHash = await ComputePackPathHashAsync(item.Source);
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (!ledger.TryGetValue(item.Key, out var priorSharedHash))
                {
                    progress.Report(((++completed * 100) / total, $"Личный пак «{Path.GetFileName(item.Source)}» сохранён; одноимённый общий пропущен."));
                    continue;
                }
                var currentHash = await ComputePackPathHashAsync(target);
                if (!currentHash.Equals(priorSharedHash, StringComparison.OrdinalIgnoreCase))
                {
                    ledger.Remove(item.Key);
                    progress.Report(((++completed * 100) / total, $"Личный пак «{Path.GetFileName(item.Source)}» не перезаписан."));
                    continue;
                }
                if (currentHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    completed++;
                    ledger[item.Key] = sourceHash;
                    continue;
                }
                try { await CopySharedPackEntryAsync(item.Source, target, replace: true); }
                catch (IOException)
                {
                    progress.Report(((++completed * 100) / total, $"Не удалось обновить общий пак «{Path.GetFileName(item.Source)}» — файл занят."));
                    continue;
                }
            }
            else
            {
                try { await CopySharedPackEntryAsync(item.Source, target, replace: false); }
                catch (IOException)
                {
                    progress.Report(((++completed * 100) / total, $"Не удалось скопировать «{Path.GetFileName(item.Source)}»."));
                    continue;
                }
            }
            ledger[item.Key] = sourceHash;
            progress.Report(((++completed * 100) / total, $"Общий пак: {Path.GetFileName(item.Source)}"));
        }

        foreach (var key in ledger.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
        {
            var target = SafeChildPath(instancePath, key);
            try
            {
                if ((File.Exists(target) || Directory.Exists(target)) && (await ComputePackPathHashAsync(target)).Equals(ledger[key], StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true); else File.Delete(target);
                }
                ledger.Remove(key);
            }
            catch (IOException) { progress.Report((Math.Min(99, ++completed * 100 / total), $"Пропущено удаление занятого пака {Path.GetFileName(target)}.")); }
        }
        await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
        progress.Report((100, "Общие ресурспаки и шейдеры доступны этому инстансу."));
    }

    private async Task CleanupSyncedSharedPacksAsync(string instancePath)
    {
        var ledgerPath = Path.Combine(instancePath, ".pistol-shared-content.json");
        if (!File.Exists(ledgerPath)) return;
        Dictionary<string, string> ledger;
        try { ledger = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(ledgerPath)) ?? new(); }
        catch { return; }
        var keepForRetry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, expectedHash) in ledger)
        {
            try
            {
                var target = SafeChildPath(instancePath, key);
                if (!File.Exists(target) && !Directory.Exists(target)) continue;
                // Keep entries edited while the game was running; those are now instance-local packs.
                if (!(await ComputePackPathHashAsync(target)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true); else File.Delete(target);
            }
            catch (IOException) { keepForRetry[key] = expectedHash; }
            catch (UnauthorizedAccessException) { keepForRetry[key] = expectedHash; }
        }
        if (keepForRetry.Count == 0) File.Delete(ledgerPath);
        else await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(keepForRetry, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> ComputePackPathHashAsync(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory())) > 0) hash.AppendData(buffer, 0, read);
        }
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(path, f), StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file)));
                await using var stream = File.OpenRead(file);
                var buffer = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory())) > 0) hash.AppendData(buffer, 0, read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopySharedPackEntryAsync(string source, string target, bool replace)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!replace && File.Exists(target)) return;
            var temporary = target + ".sync-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await input.CopyToAsync(output);
                File.Move(temporary, target, replace);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
            return;
        }
        if (!Directory.Exists(source)) return;
        if (Directory.Exists(target) && replace) Directory.Delete(target, recursive: true);
        if (File.Exists(target) && replace) File.Delete(target);
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            await CopySharedPackEntryAsync(entry, Path.Combine(target, Path.GetFileName(entry)), replace);
        }
    }

    private void ShowLaunchFailure(GameInstance instance, int exitCode, string logPath)
    {
        var log = File.Exists(logPath) ? string.Join(Environment.NewLine, File.ReadLines(logPath).TakeLast(80)) : "Java завершилась без вывода. Подробный лог пока пуст.";
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var open = new Button { Content = "Открыть папку инстанса", Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(instance.Path) { UseShellExecute = true });
        var close = new Button { Content = "Закрыть", IsDefault = true };
        buttons.Children.Add(open); buttons.Children.Add(close); panel.Children.Add(buttons);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(new TextBlock { Text = $"Minecraft завершился сразу после запуска (код {exitCode}). Лог: logs/pistol-launcher.log", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var details = new TextBox { Text = log, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(17, 20, 18)), Foreground = new SolidColorBrush(Color.FromRgb(220, 226, 221)) };
        Grid.SetRow(details, 1); body.Children.Add(details); panel.Children.Add(body);
        var dialog = new Window { Title = "Minecraft не запустился", Owner = this, Width = 760, Height = 500, MinWidth = 500, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, Content = panel };
        ApplyLauncherChrome(dialog, "Ошибка запуска");
        close.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private async Task<string> EnsureAuthlibInjectorAsync()
    {
        const string apiUrl = "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var releaseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = releaseDoc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Authlib-injector release has no version tag.");
        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => (item.GetProperty("name").GetString() ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidDataException("GitHub release did not include the authlib-injector JAR.");
        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps || !assetUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Authlib-injector download URL is not a trusted HTTPS GitHub URL.");

        var jarDirectory = Path.Combine(_root, "runtime", "authlib-injector");
        var jarPath = Path.Combine(jarDirectory, "authlib-injector-" + new string(tag.Where(char.IsLetterOrDigit).ToArray()) + ".jar");
        if (File.Exists(jarPath) && await IsValidJarAsync(jarPath)) return jarPath;
        Directory.CreateDirectory(jarDirectory);
        var temporaryPath = jarPath + ".download";
        try
        {
            using var jarResponse = await Http.GetAsync(assetUri, HttpCompletionOption.ResponseHeadersRead);
            jarResponse.EnsureSuccessStatusCode();
            await using (var input = await jarResponse.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output);
            if (!await IsValidJarAsync(temporaryPath)) throw new InvalidDataException("Downloaded authlib-injector file is not a valid JAR.");
            File.Move(temporaryPath, jarPath, overwrite: true);
            return jarPath;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static async Task<bool> IsValidJarAsync(string path)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Any(entry => entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    private static string? GetModrinthLoaderId(string loader)
    {
        var normalized = loader.Trim().ToLowerInvariant();
        if (normalized.StartsWith("fabric", StringComparison.Ordinal)) return "fabric";
        if (normalized.StartsWith("quilt", StringComparison.Ordinal)) return "quilt";
        if (normalized.StartsWith("neoforge", StringComparison.Ordinal)) return "neoforge";
        if (normalized.StartsWith("forge", StringComparison.Ordinal)) return "forge";
        return null;
    }

    private async Task PrepareOfflineSkinForInstanceAsync(GameInstance instance, LauncherProfile profile)
    {
        var skinPath = !string.IsNullOrWhiteSpace(profile.SkinPath)
            ? profile.SkinPath
            : Path.Combine(_launcherRoot, ".assets", "skins", profile.Id + ".png");
        if (!File.Exists(skinPath)) return;
        var loaderId = GetModrinthLoaderId(instance.Loader);
        if (loaderId is null)
        {
            ShowMessageBox(this,
                L("Локальный скин офлайн-профиля требует Fabric, Quilt, Forge или NeoForge. В этом Vanilla-инстансе игра запустится без пользовательского скина.", "An offline profile's local skin needs Fabric, Quilt, Forge, or NeoForge. This Vanilla instance will launch without the custom skin."),
                L("Скин", "Skin"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var versionQuery = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.GameVersion }));
        var loaderQuery = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderId }));
        var apiUrl = $"https://api.modrinth.com/v2/project/customskinloader/version?game_versions={versionQuery}&loaders={loaderQuery}";
        using var response = await Http.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        using var versionsDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = versionsDoc.RootElement.EnumerateArray().FirstOrDefault();
        if (version.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(L($"CustomSkinLoader ещё не поддерживает Minecraft {instance.GameVersion} ({instance.Loader}).", $"CustomSkinLoader does not support Minecraft {instance.GameVersion} ({instance.Loader}) yet."));
        var file = version.GetProperty("files").EnumerateArray()
            .FirstOrDefault(item => item.TryGetProperty("primary", out var primary) && primary.GetBoolean());
        if (file.ValueKind != JsonValueKind.Object) file = version.GetProperty("files").EnumerateArray().FirstOrDefault();
        var modUrl = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "";
        if (!Uri.TryCreate(modUrl, UriKind.Absolute, out var modUri) || modUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Modrinth did not provide a valid HTTPS download for CustomSkinLoader.");
        var expectedSha1 = file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null;

        var modsDirectory = Path.Combine(instance.Path, "mods");
        Directory.CreateDirectory(modsDirectory);
        var modPath = Path.Combine(modsDirectory, "pistollauncher-customskinloader.jar");
        var alreadyInstalled = Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .Any(path => !path.Equals(modPath, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).Contains("customskinloader", StringComparison.OrdinalIgnoreCase));
        if (!alreadyInstalled && !await IsValidFileAsync(modPath, expectedSha1, CancellationToken.None))
        {
            StatusText.Text = L("Подключаю локальный скин…", "Setting up the local skin…");
            var temporaryMod = modPath + ".download";
            try
            {
                using var modResponse = await Http.GetAsync(modUri, HttpCompletionOption.ResponseHeadersRead);
                modResponse.EnsureSuccessStatusCode();
                await using (var input = await modResponse.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temporaryMod, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await input.CopyToAsync(output);
                if (!await IsValidFileAsync(temporaryMod, expectedSha1, CancellationToken.None))
                    throw new InvalidDataException("CustomSkinLoader download failed its SHA-1 check.");
                File.Move(temporaryMod, modPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryMod)) File.Delete(temporaryMod); }
        }
        else if (!alreadyInstalled && !await IsValidJarAsync(modPath))
        {
            File.Delete(modPath);
            throw new InvalidDataException("The installed CustomSkinLoader file is not a valid JAR.");
        }

        var safeUsername = new string(profile.Name.Where(char.IsLetterOrDigit).Take(16).ToArray());
        if (safeUsername.Length == 0) throw new InvalidDataException("The offline profile name cannot be used as a local skin filename.");
        var localSkinDirectory = Path.Combine(instance.Path, "CustomSkinLoader", "LocalSkin", "skins");
        Directory.CreateDirectory(localSkinDirectory);
        File.Copy(skinPath, Path.Combine(localSkinDirectory, safeUsername + ".png"), overwrite: true);
    }

    private async Task<Process> StartInstanceAsync(GameInstance instance)
    {
        var versionsDir = Path.Combine(instance.Path, "versions");
        if (!Directory.Exists(versionsDir)) throw new DirectoryNotFoundException("В инстансе нет папки versions. Сначала скачай Minecraft или установи сборку.");
        var candidates = Directory.EnumerateFiles(versionsDir, "*.json", SearchOption.AllDirectories).ToArray();
        JsonElement? baseMetadata = null;
        JsonElement? loaderMetadata = null;
        foreach (var file in candidates)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && id.GetString() == instance.GameVersion)
            {
                baseMetadata = root.Clone();
                continue;
            }
            if (root.TryGetProperty("inheritsFrom", out var parent) && parent.GetString() == instance.GameVersion)
            {
                loaderMetadata = root.Clone();
                continue;
            }
        }
        if (baseMetadata is null) throw new FileNotFoundException($"Не найдены метаданные Minecraft {instance.GameVersion}. Переустанови инстанс.");
        {
            var game = baseMetadata.Value;
            var loader = loaderMetadata;
            var versionId = loader is { } l && l.TryGetProperty("id", out var loaderId) ? loaderId.GetString() ?? instance.GameVersion : instance.GameVersion;
            var clientJar = Path.Combine(versionsDir, instance.GameVersion, instance.GameVersion + ".jar");
            if (!File.Exists(clientJar)) throw new FileNotFoundException("Не найден клиентский JAR. Переустанови эту версию.", clientJar);
            var useEly = _activeProfile?.Type == "ely";
            if (useEly) await RefreshElyTokenIfNeededAsync();
            else if (_activeProfile is { Type: "offline" } offlineProfile)
                await PrepareOfflineSkinForInstanceAsync(instance, offlineProfile);
            var username = useEly ? _elyAuth.Username : _activeProfile?.Name ?? "Player";
            if (username.Length == 0) username = "Player";
            var uuid = useEly && !string.IsNullOrWhiteSpace(_elyAuth.Uuid) ? _elyAuth.Uuid.Replace("-", "", StringComparison.Ordinal) : OfflineUuid(username);
            var accessToken = useEly ? _elyAuth.AccessToken : "0";
            var nativesDir = Path.Combine(instance.Path, "natives", instance.GameVersion);
            Directory.CreateDirectory(nativesDir);
            var libraries = new List<JsonElement>();
            foreach (var source in new[] { game, loader ?? default })
                if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("libraries", out var libs))
                    libraries.AddRange(libs.EnumerateArray().ToArray());
            var classpath = new List<string> { clientJar };
            var nativeJars = new List<string>();
            foreach (var library in libraries)
            {
                if (!IsAllowedOnWindows(library)) continue;
                if (library.TryGetProperty("downloads", out var downloads))
                {
                    if (downloads.TryGetProperty("artifact", out var artifact) && artifact.TryGetProperty("path", out var artifactPath))
                    {
                        var path = Path.Combine(_root, "libraries", artifactPath.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path)) classpath.Add(path);
                    }
                    if (downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        foreach (var classifier in classifiers.EnumerateObject())
                        {
                            if (!classifier.Name.StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!classifier.Value.TryGetProperty("path", out var nativePath)) continue;
                            var path = Path.Combine(_root, "libraries", nativePath.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(path)) nativeJars.Add(path);
                        }
                    }
                }
                else if (library.TryGetProperty("name", out var name))
                {
                    var path = MavenPath(name.GetString() ?? "");
                    if (path is not null && File.Exists(path)) classpath.Add(path);
                }
            }
            foreach (var jar in nativeJars) ExtractNatives(jar, nativesDir);
            var assetsRoot = Path.Combine(_root, "assets");
            var assetIndex = game.TryGetProperty("assetIndex", out var assetInfo) && assetInfo.TryGetProperty("id", out var assetId) ? assetId.GetString() ?? "" : "";
            var replacements = new Dictionary<string, string>
            {
                ["${auth_player_name}"] = username, ["${version_name}"] = versionId,
                ["${game_directory}"] = instance.Path, ["${assets_root}"] = assetsRoot,
                ["${assets_index_name}"] = assetIndex, ["${auth_uuid}"] = uuid,
                ["${auth_access_token}"] = accessToken, ["${auth_session}"] = useEly ? accessToken : $"token:{uuid}:{accessToken}", ["${user_type}"] = useEly ? "mojang" : "legacy",
                ["${version_type}"] = "release", ["${user_properties}"] = "{}",
                ["${clientid}"] = "", ["${auth_xuid}"] = "", ["${classpath}"] = string.Join(Path.PathSeparator, classpath),
                ["${classpath_separator}"] = Path.PathSeparator.ToString(), ["${library_directory}"] = Path.Combine(_root, "libraries"),
                ["${natives_directory}"] = nativesDir, ["${launcher_name}"] = "PistolLauncher", ["${launcher_version}"] = "1.0"
            };
            var gameArgs = new List<string>();
            AddArguments(gameArgs, game, "game", replacements);
            if (loader is { } loaderRoot) AddArguments(gameArgs, loaderRoot, "game", replacements);
            if (gameArgs.Count == 0 && game.TryGetProperty("minecraftArguments", out var legacyArgs))
                gameArgs.AddRange(ExpandLegacyArguments(legacyArgs.GetString() ?? "", replacements));
            if (gameArgs.Count == 0) throw new InvalidDataException("У версии Minecraft отсутствуют аргументы запуска.");

            var requiredJavaMajor = game.TryGetProperty("javaVersion", out var javaInfo) && javaInfo.TryGetProperty("majorVersion", out var javaMajorValue)
                ? javaMajorValue.GetInt32()
                : GuessJavaMajor(instance.GameVersion);
            StatusText.Text = $"Проверяю Java {requiredJavaMajor}…";
            var java = await FindJavaAsync(requiredJavaMajor);
            var classpathValue = string.Join(Path.PathSeparator, classpath);
            var modernJvm = new List<string>();
            AddArguments(modernJvm, game, "jvm", replacements);
            if (loader is { } jvmLoader) AddArguments(modernJvm, jvmLoader, "jvm", replacements);
            var jvmArgs = new List<string> { $"-Xmx{Math.Clamp(_preferences.RamGb, 1, 32)}G", "-Xms512M" };
            foreach (var arg in modernJvm)
            {
                if (arg is "-cp" or "-classpath" || string.Equals(arg, classpathValue, StringComparison.Ordinal)) continue;
                if (arg.StartsWith("-Djava.library.path=", StringComparison.Ordinal)) continue;
                jvmArgs.Add(arg);
            }
            jvmArgs.Add("-Djava.library.path=" + nativesDir);
            if (useEly)
            {
                StatusText.Text = L("Подключаю Ely.by и скин…", "Setting up Ely.by authentication and skin…");
                var authlibInjector = await EnsureAuthlibInjectorAsync();
                jvmArgs.Add("-javaagent:" + authlibInjector + "=ely.by");
            }
            jvmArgs.Add("-cp");
            jvmArgs.Add(classpathValue);
            var mainClass = loader is { } profile && profile.TryGetProperty("mainClass", out var profileMain) ? profileMain.GetString() : null;
            if (string.IsNullOrWhiteSpace(mainClass)) mainClass = game.TryGetProperty("mainClass", out var main) ? main.GetString() : null;
            if (string.IsNullOrWhiteSpace(mainClass)) throw new InvalidDataException("В метаданных версии не указан главный класс.");
            jvmArgs.Add(mainClass);
            jvmArgs.AddRange(gameArgs);
            var start = new ProcessStartInfo(java)
            {
                WorkingDirectory = instance.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in jvmArgs) start.ArgumentList.Add(arg);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Windows не смог запустить Java.");
            var key = Path.GetFullPath(instance.Path);
            var logPath = Path.Combine(instance.Path, "logs", "pistol-launcher.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            void SaveOutput(string stream, string? line)
            {
                if (line is null) return;
                try { lock (process) File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] [{stream}] {line}{Environment.NewLine}"); } catch { }
            }
            process.OutputDataReceived += (_, args) => SaveOutput("out", args.Data);
            process.ErrorDataReceived += (_, args) => SaveOutput("err", args.Data);
            process.EnableRaisingEvents = true;
            _runningInstances[key] = process;
            process.Exited += (_, _) =>
            {
                _ = Task.Run(() => CleanupSyncedSharedPacksAsync(instance.Path));
                Dispatcher.BeginInvoke(() =>
                {
                    if (_runningInstances.TryGetValue(key, out var current) && ReferenceEquals(current, process)) _runningInstances.Remove(key);
                    if (StatusText.Text.StartsWith("Minecraft запущен", StringComparison.Ordinal))
                        StatusText.Text = $"Minecraft закрыт (код {process.ExitCode})";
                });
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
    }

    private async Task<string> FindJavaAsync(int requiredMajor)
    {
        var runtimeRoot = Path.Combine(_root, "runtime");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_preferences.JavaPath))
        {
            candidates.Add(_preferences.JavaPath);
            candidates.Add(Path.Combine(_preferences.JavaPath, "bin", "java.exe"));
            candidates.Add(Path.Combine(_preferences.JavaPath, "java.exe"));
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate) && await ReadJavaMajorAsync(candidate) == requiredMajor) return candidate;

        var managedJava = Path.Combine(runtimeRoot, $"java-{requiredMajor}", "bin", "java.exe");
        if (File.Exists(managedJava) && await ReadJavaMajorAsync(managedJava) == requiredMajor) return managedJava;

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, "java.exe")));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate) && await ReadJavaMajorAsync(candidate) == requiredMajor) return candidate;

        return await InstallJavaRuntimeAsync(requiredMajor);
    }

    private async Task<string> InstallJavaRuntimeAsync(int major)
    {
        StatusText.Text = $"Скачиваю Temurin Java {major}…";
        var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse&release_type=ga";
        using var metadataResponse = await Http.GetAsync(api, HttpCompletionOption.ResponseHeadersRead);
        metadataResponse.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        var binaries = new List<JsonElement>();
        foreach (var release in metadata.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("binary", out var binary) && binary.ValueKind == JsonValueKind.Object)
                binaries.Add(binary);
            if (release.TryGetProperty("binaries", out var binaryArray) && binaryArray.ValueKind == JsonValueKind.Array)
                binaries.AddRange(binaryArray.EnumerateArray().ToArray());
        }
        var package = binaries
            .Where(binary => binary.TryGetProperty("architecture", out var arch) && arch.GetString() == "x64"
                && binary.TryGetProperty("os", out var os) && os.GetString() == "windows"
                && binary.TryGetProperty("image_type", out var imageType) && imageType.GetString() == "jre")
            .Select(binary => binary.TryGetProperty("package", out var item) ? item : default)
            .FirstOrDefault(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("link", out var link) && !string.IsNullOrWhiteSpace(link.GetString())
                && item.TryGetProperty("checksum", out var checksum) && !string.IsNullOrWhiteSpace(checksum.GetString()));
        if (package.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Adoptium не вернул архив Java {major} для Windows x64.");
        var downloadUri = new Uri(package.GetProperty("link").GetString()!);
        if (downloadUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Ссылка на Java должна использовать HTTPS.");
        var expectedSha256 = package.GetProperty("checksum").GetString()!;
        var runtimeRoot = Path.Combine(_root, "runtime");
        var tempRoot = Path.Combine(runtimeRoot, $"java-{major}.installing");
        var archivePath = Path.Combine(runtimeRoot, $"java-{major}.zip.part");
        Directory.CreateDirectory(runtimeRoot);
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        try
        {
            using (var downloadResponse = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead))
            {
                downloadResponse.EnsureSuccessStatusCode();
                await using var source = await downloadResponse.Content.ReadAsStreamAsync();
                await using var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await source.CopyToAsync(destination);
            }
            await using (var stream = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма Java не совпала; архив удалён.");
            }
            Directory.CreateDirectory(tempRoot);
            ZipFile.ExtractToDirectory(archivePath, tempRoot, overwriteFiles: true);
            var javaExe = Directory.EnumerateFiles(tempRoot, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase));
            if (javaExe is null) throw new InvalidDataException("В архиве Temurin не найден bin/java.exe.");
            var extractedHome = Directory.GetParent(Path.GetDirectoryName(javaExe)!)!.FullName;
            var finalHome = Path.Combine(runtimeRoot, $"java-{major}");
            if (Directory.Exists(finalHome)) Directory.Delete(finalHome, recursive: true);
            Directory.Move(extractedHome, finalHome);
            var installedJava = Path.Combine(finalHome, "bin", "java.exe");
            if (await ReadJavaMajorAsync(installedJava) != major) throw new InvalidDataException($"Распакованная Java не соответствует требуемой версии {major}.");
            StatusText.Text = $"Java {major} установлена в minecraft/runtime.";
            return installedJava;
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<int?> ReadJavaMajorAsync(string javaPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(javaPath, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            });
            if (process is null) return null;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
            var versionText = await stderr + " " + await stdout;
            var match = System.Text.RegularExpressions.Regex.Match(versionText, "version \\\"(?<v>[^\\\"]+)\\\"|openjdk (?<v>\\d+(?:\\.\\d+)*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var version = match.Groups["v"].Value;
            var parts = version.Split('.');
            return parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor)
                ? legacyMajor
                : int.TryParse(parts[0], out var major) ? major : null;
        }
        catch { return null; }
    }

    private static int GuessJavaMajor(string gameVersion)
    {
        if (Version.TryParse(gameVersion, out var version))
        {
            if (version.Major == 1 && version.Minor <= 16) return 8;
            if (version.Major == 1 && version.Minor == 17) return 16;
            if (version.Major == 1 && version.Minor <= 20 && version.Build < 5) return 17;
        }
        return 21;
    }

    private void AddArguments(List<string> output, JsonElement metadata, string group, Dictionary<string, string> replacements)
    {
        if (!metadata.TryGetProperty("arguments", out var args) || !args.TryGetProperty(group, out var values)) return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String) { output.Add(ReplaceArgument(value.GetString()!, replacements)); continue; }
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("value", out var argValue)) continue;
            if (value.TryGetProperty("rules", out var rules) && !RulesAllow(rules)) continue;
            if (argValue.ValueKind == JsonValueKind.String) output.Add(ReplaceArgument(argValue.GetString()!, replacements));
            else if (argValue.ValueKind == JsonValueKind.Array)
                foreach (var item in argValue.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) output.Add(ReplaceArgument(item.GetString()!, replacements));
        }
    }

    private static bool RulesAllow(JsonElement rules)
    {
        var featureValues = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = !rule.TryGetProperty("os", out var os) || !os.TryGetProperty("name", out var name) || name.GetString() == "windows";
            if (matches && rule.TryGetProperty("features", out var features))
                foreach (var feature in features.EnumerateObject())
                    if (!featureValues.TryGetValue(feature.Name, out var actual) || feature.Value.ValueKind != JsonValueKind.True && feature.Value.ValueKind != JsonValueKind.False || actual != feature.Value.GetBoolean()) { matches = false; break; }
            if (matches) allowed = rule.TryGetProperty("action", out var action) && action.GetString() == "allow";
        }
        return allowed;
    }

    private static string ReplaceArgument(string value, Dictionary<string, string> replacements)
    {
        foreach (var pair in replacements) value = value.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        if (value.Contains("${", StringComparison.Ordinal)) throw new InvalidDataException("Для запуска не обработан аргумент: " + value);
        return value;
    }

    private IEnumerable<string> ExpandLegacyArguments(string value, Dictionary<string, string> replacements)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(value, "\\\"([^\\\"]*)\\\"|(\\S+)");
        foreach (System.Text.RegularExpressions.Match match in matches) yield return ReplaceArgument(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, replacements);
    }

    private string? MavenPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return null;
        var artifactVersion = parts[2]; var extension = "jar"; var at = artifactVersion.IndexOf('@');
        if (at >= 0) { extension = artifactVersion[(at + 1)..]; artifactVersion = artifactVersion[..at]; }
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        var relative = string.Join('/', parts[0].Split('.')) + "/" + parts[1] + "/" + artifactVersion + "/" + parts[1] + "-" + artifactVersion + classifier + "." + extension;
        return Path.Combine(_root, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void ExtractNatives(string jar, string destination)
    {
        using var archive = ZipFile.OpenRead(jar);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == "..")) continue;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async void AddInstanceLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_versions.Count == 0) await LoadVersionsAsync();
        if (_versions.Count == 0) { ShowMessageBox(this, "Не удалось загрузить каталог Minecraft.", "PistolLauncher"); return; }
        var releaseVersions = new ObservableCollection<GameVersion>(_versions.Where(v => v.Type == "release"));
        var versionBox = new ComboBox { ItemsSource = releaseVersions, DisplayMemberPath = "Id", SelectedItem = releaseVersions.FirstOrDefault(), Margin = new Thickness(0, 4, 0, 10) };
        var loaderBox = new ComboBox { ItemsSource = new[] { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
        var includePrereleases = new CheckBox { Content = "Альфа, бета и снапшоты", Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 12) };
        includePrereleases.Checked += (_, _) => { loaderBox.IsEnabled = false; versionBox.ItemsSource = new ObservableCollection<GameVersion>(_versions.Where(v => v.Type is "release" or "snapshot" or "old_beta" or "old_alpha")); versionBox.SelectedIndex = 0; };
        includePrereleases.Unchecked += (_, _) => { loaderBox.IsEnabled = true; versionBox.ItemsSource = new ObservableCollection<GameVersion>(_versions.Where(v => v.Type == "release")); versionBox.SelectedIndex = 0; };
        var nameBox = new TextBox { Text = "Minecraft " + (releaseVersions.FirstOrDefault()?.Id ?? _versions[0].Id) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Название инстанса" }); panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Версия Minecraft", Margin = new Thickness(0, 12, 0, 0) }); panel.Children.Add(versionBox);
        panel.Children.Add(new TextBlock { Text = "Загрузчик", Margin = new Thickness(0, 3, 0, 0) }); panel.Children.Add(loaderBox);
        panel.Children.Add(includePrereleases);
        panel.Children.Add(new TextBlock { Text = "Все загрузчики устанавливаются через официальные профили или установщики.", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var create = new Button { Content = "Создать и скачать", Background = AccentBrush, Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = AccentBrush, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Отмена" };
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        var dialog = new Window { Title = "Новый инстанс", Width = 470, Height = 430, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        ApplyLauncherChrome(dialog, "Новый инстанс");
        cancel.Click += (_, _) => dialog.DialogResult = false;
        create.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || versionBox.SelectedItem is not GameVersion version) return;
        var selectedLoader = includePrereleases.IsChecked == true ? "Vanilla" : loaderBox.SelectedItem?.ToString() ?? "Vanilla";
        var name = new string(nameBox.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        if (name.Length == 0) { ShowMessageBox(this, "Введи название инстанса."); return; }
        var instancesDir = Path.Combine(_root, "instances");
        var instanceDir = Path.Combine(instancesDir, name);
        if (Directory.Exists(instanceDir)) { ShowMessageBox(this, "Инстанс с таким названием уже существует."); return; }
        Pages.SelectedIndex = 1;
        Directory.CreateDirectory(instanceDir);
        _downloadCts = new CancellationTokenSource();
        DownloadProgress.Value = 0;
        StatusText.Text = $"Создаю {name} · Minecraft {version.Id}…";
        DetailText.Text = "";
        try
        {
            var progress = new Progress<(int Percent, string Message)>(p => { DownloadProgress.Value = p.Percent; DetailText.Text = p.Message; });
            await InstallVersionAsync(version, instanceDir, progress, _downloadCts.Token, selectedLoader != "Vanilla");
            var loaderVersion = selectedLoader == "Vanilla" ? "" : await InstallLoaderProfileAsync(version.Id, selectedLoader, instanceDir, progress, _downloadCts.Token);
            await File.WriteAllTextAsync(Path.Combine(instanceDir, ".pistol-instance.json"), JsonSerializer.Serialize(new { name, gameVersion = version.Id, loader = selectedLoader, loaderVersion }));
            StatusText.Text = $"Инстанс «{name}» создан";
            DetailText.Text = $"Файлы Minecraft {version.Id} находятся в minecraft/instances/{name}";
            RefreshInstances();
            Pages.SelectedIndex = 1;
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Path.Equals(instanceDir, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { StatusText.Text = "Создание инстанса отменено"; DetailText.Text = "Незавершённую папку можно удалить вручную."; }
        catch (Exception ex) { StatusText.Text = "Не удалось создать инстанс"; DetailText.Text = ex.Message; }
        finally { _downloadCts.Dispose(); _downloadCts = null; }
    }

    private async void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_versions.Count == 0) await LoadVersionsAsync();
        if (_versions.Count == 0) { ShowMessageBox(this, "Не удалось загрузить каталог Minecraft.", "PistolLauncher"); return; }

        var releases = new ObservableCollection<GameVersion>(_versions.Where(v => v.Type == "release"));
        var versionBox = new ComboBox { ItemsSource = releases, DisplayMemberPath = "Id", SelectedItem = releases.FirstOrDefault(), Margin = new Thickness(0, 4, 0, 10) };
        var loaderBox = new ComboBox { ItemsSource = new[] { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
        var prereleases = new CheckBox { Content = "Альфа, бета и снапшоты", Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 12) };
        prereleases.Checked += (_, _) => { loaderBox.IsEnabled = false; versionBox.ItemsSource = new ObservableCollection<GameVersion>(_versions.Where(v => v.Type is "release" or "snapshot" or "old_beta" or "old_alpha")); versionBox.SelectedIndex = 0; };
        prereleases.Unchecked += (_, _) => { loaderBox.IsEnabled = true; versionBox.ItemsSource = releases; versionBox.SelectedItem = releases.FirstOrDefault(); };
        var nameBox = new TextBox { Text = "Minecraft " + (releases.FirstOrDefault()?.Id ?? _versions[0].Id) };
        var minecraftPage = new StackPanel { Margin = new Thickness(26) };
        minecraftPage.Children.Add(new TextBlock { Text = "Новый Minecraft-инстанс", FontSize = 25, FontWeight = FontWeights.Bold, Foreground = AccentBrush });
        minecraftPage.Children.Add(new TextBlock { Text = "Создай чистую игру или отдельный профиль с загрузчиком.", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), Margin = new Thickness(0, 5, 0, 18) });
        minecraftPage.Children.Add(new TextBlock { Text = "Название инстанса" }); minecraftPage.Children.Add(nameBox);
        minecraftPage.Children.Add(new TextBlock { Text = "Версия Minecraft", Margin = new Thickness(0, 10, 0, 0) }); minecraftPage.Children.Add(versionBox);
        minecraftPage.Children.Add(new TextBlock { Text = "Загрузчик", Margin = new Thickness(0, 5, 0, 0) }); minecraftPage.Children.Add(loaderBox);
        minecraftPage.Children.Add(prereleases);
        var createStatus = new TextBlock { Text = "CurseForge- и Modrinth-сборки можно найти в соседних разделах.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), Margin = new Thickness(0, 8, 0, 14) };
        minecraftPage.Children.Add(createStatus);
        var create = new Button { Content = "Создать и скачать", HorizontalAlignment = HorizontalAlignment.Right, Background = AccentBrush, Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = AccentBrush };
        minecraftPage.Children.Add(create);

        var nav = new StackPanel { Margin = new Thickness(12, 18, 12, 12) };
        foreach (var item in new[] { (Name: "Minecraft", Icon: "minecraft.png"), (Name: "CurseForge", Icon: "curseforge.png"), (Name: "Modrinth", Icon: "modrinth.png") })
        {
            var icon = new Image { Width = 30, Height = 30, Stretch = System.Windows.Media.Stretch.UniformToFill };
            var iconPath = Path.Combine(_launcherRoot, ".assets", "textures", item.Icon);
            if (File.Exists(iconPath))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(iconPath); bitmap.EndInit();
                icon.Source = bitmap;
            }
            else icon.Source = null;
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromRgb(36, 41, 37)), ClipToBounds = true, Child = icon };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(badge); row.Children.Add(new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            nav.Children.Add(new Button { Content = row, Tag = item.Name, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 8, 6, 8), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 6) });
        }
        var cfPage = BuildPackSearchPage("CurseForge", out var cfQuery, out var cfStatus, out var cfResults, out var cfSearch, out var cfInstall, out var cfKey);
        var mrPage = BuildPackSearchPage("Modrinth", out var mrQuery, out var mrStatus, out var mrResults, out var mrSearch, out var mrInstall, out _);
        var pages = new List<UIElement> { minecraftPage, cfPage, mrPage };
        var pageHost = new Grid(); foreach (var page in pages) pageHost.Children.Add(page);
        var shell = new Grid(); shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(20, 22, 21)), BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 50)), BorderThickness = new Thickness(0, 0, 1, 0), Child = nav });
        Grid.SetColumn(pageHost, 1); shell.Children.Add(pageHost);
        var dialog = new Window { Title = "Добавить инстанс", Width = 980, Height = 690, MinWidth = 850, MinHeight = 560, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = shell, Background = new SolidColorBrush(Color.FromRgb(16, 17, 17)), Foreground = Brushes.White, ResizeMode = ResizeMode.CanResize };
        GameVersion? selectedVersion = null;
        string selectedLoader = "Vanilla", selectedName = "";
        var searched = new HashSet<string>(StringComparer.Ordinal);
        var packOffsets = new Dictionary<string, int>(StringComparer.Ordinal) { ["CurseForge"] = 0, ["Modrinth"] = 0 };
        var packHasMore = new Dictionary<string, bool>(StringComparer.Ordinal) { ["CurseForge"] = true, ["Modrinth"] = true };
        var packLoading = new HashSet<string>(StringComparer.Ordinal);
        var navButtons = nav.Children.OfType<Button>().ToArray();
        async Task SearchPacksAsync(string source, TextBox query, TextBlock status, ListBox results, bool loadMore = false)
        {
            if (packLoading.Contains(source) || (loadMore && !packHasMore[source])) return;
            packLoading.Add(source);
            if (!loadMore) { packOffsets[source] = 0; packHasMore[source] = true; results.ItemsSource = null; }
            status.Text = source == "CurseForge" && string.IsNullOrWhiteSpace(_curseForgeKey)
                ? L("Нужен CurseForge API key. Настрой его кнопкой ниже.", "A CurseForge API key is required. Configure it using the button below.")
                : loadMore ? L($"Загружаю ещё сборки с {source}…", $"Loading more modpacks from {source}…")
                : L($"Ищу сборки на {source}…", $"Searching {source} modpacks…");
            try
            {
                var page = packOffsets[source];
                var found = source == "Modrinth" ? await SearchModrinthAsync(query.Text.Trim(), "modpack", "", offset: page) : await SearchCurseForgeAsync(query.Text.Trim(), "modpack", page);
                var combined = loadMore && results.ItemsSource is ObservableCollection<ModProject> current ? current : new ObservableCollection<ModProject>();
                if (!loadMore) results.ItemsSource = combined;
                foreach (var project in found) combined.Add(project);
                packOffsets[source] += found.Count;
                packHasMore[source] = found.Count >= 50;
                foreach (var project in found) _ = CacheProjectAvatarAsync(project);
                status.Text = IsEnglish
                    ? $"Found modpacks: {combined.Count}" + (packHasMore[source] ? " · scroll down to load more" : "")
                    : $"Найдено сборок: {combined.Count}" + (packHasMore[source] ? " · прокрути вниз для продолжения" : "");
                searched.Add(source);
            }
            catch (Exception ex) { status.Text = L("Поиск не удался: ", "Search failed: ") + ex.Message; }
            finally { packLoading.Remove(source); }
        }
        void AttachPackPagination(ListBox list, string source, TextBox query, TextBlock status)
        {
            list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(async (_, args) =>
            {
                if (args.VerticalChange > 0 && args.VerticalOffset + args.ViewportHeight >= args.ExtentHeight - 3)
                    await SearchPacksAsync(source, query, status, list, loadMore: true);
            }), true);
        }
        AttachPackPagination(cfResults, "CurseForge", cfQuery, cfStatus);
        AttachPackPagination(mrResults, "Modrinth", mrQuery, mrStatus);
        void SelectPage(int index)
        {
            for (var i = 0; i < pages.Count; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < navButtons.Length; i++)
            {
                navButtons[i].Background = i == index ? new SolidColorBrush(Color.FromRgb(36, 41, 37)) : Brushes.Transparent;
                navButtons[i].BorderBrush = i == index ? AccentBrush : Brushes.Transparent;
                navButtons[i].Foreground = i == index ? AccentBrush : Brushes.White;
            }
            var source = index == 1 ? "CurseForge" : index == 2 ? "Modrinth" : "";
            if (source.Length > 0 && !searched.Contains(source)) _ = SearchPacksAsync(source, source == "CurseForge" ? cfQuery : mrQuery, source == "CurseForge" ? cfStatus : mrStatus, source == "CurseForge" ? cfResults : mrResults);
        }
        for (var i = 0; i < navButtons.Length; i++)
        {
            var index = i; navButtons[i].Click += (_, _) => SelectPage(index);
        }
        cfSearch.Click += async (_, _) => await SearchPacksAsync("CurseForge", cfQuery, cfStatus, cfResults);
        mrSearch.Click += async (_, _) => await SearchPacksAsync("Modrinth", mrQuery, mrStatus, mrResults);
        cfQuery.KeyDown += async (_, args) => { if (args.Key == Key.Enter) await SearchPacksAsync("CurseForge", cfQuery, cfStatus, cfResults); };
        mrQuery.KeyDown += async (_, args) => { if (args.Key == Key.Enter) await SearchPacksAsync("Modrinth", mrQuery, mrStatus, mrResults); };
        cfKey.Click += (_, _) => ConfigureCurseForge_Click(this, new RoutedEventArgs());
        cfInstall.Click += async (_, _) =>
        {
            if (cfResults.SelectedItem is ModProject project) await InstallCatalogPackAsync(project, cfStatus);
            else cfStatus.Text = "Сначала выбери сборку из списка.";
        };
        mrInstall.Click += async (_, _) =>
        {
            if (mrResults.SelectedItem is ModProject project) await InstallCatalogPackAsync(project, mrStatus);
            else mrStatus.Text = "Сначала выбери сборку из списка.";
        };
        create.Click += (_, _) =>
        {
            selectedVersion = versionBox.SelectedItem as GameVersion;
            selectedLoader = prereleases.IsChecked == true ? "Vanilla" : loaderBox.SelectedItem?.ToString() ?? "Vanilla";
            selectedName = new string(nameBox.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
            if (selectedVersion is null || selectedName.Length == 0) { selectedVersion = null; createStatus.Text = "Выбери версию и введи название инстанса."; return; }
            if (Directory.Exists(Path.Combine(_root, "instances", selectedName))) { selectedVersion = null; createStatus.Text = "Инстанс с таким названием уже существует."; return; }
            dialog.DialogResult = true;
        };
        ApplyLauncherChrome(dialog, "Добавить инстанс");
        SelectPage(0);
        dialog.ShowDialog();
        if (selectedVersion is null) return;
        var instanceDir = Path.Combine(_root, "instances", selectedName);
        _downloadCts = new CancellationTokenSource();
        try
        {
            await RunWithDownloadProgressAsync($"Создание инстанса: {selectedName}", async progress =>
            {
                Pages.SelectedIndex = 1;
                Directory.CreateDirectory(instanceDir);
                StatusText.Text = $"Создаю {selectedName} · Minecraft {selectedVersion.Id}…";
                DetailText.Text = "";
                await InstallVersionAsync(selectedVersion, instanceDir, progress, _downloadCts.Token, selectedLoader != "Vanilla");
                var loaderVersion = selectedLoader == "Vanilla" ? "" : await InstallLoaderProfileAsync(selectedVersion.Id, selectedLoader, instanceDir, progress, _downloadCts.Token);
                await File.WriteAllTextAsync(Path.Combine(instanceDir, ".pistol-instance.json"), JsonSerializer.Serialize(new { name = selectedName, gameVersion = selectedVersion.Id, loader = selectedLoader, loaderVersion }));
                StatusText.Text = $"Инстанс «{selectedName}» создан";
                DetailText.Text = $"Файлы Minecraft {selectedVersion.Id} находятся в minecraft/instances/{selectedName}";
                RefreshInstances();
                InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Path.Equals(instanceDir, StringComparison.OrdinalIgnoreCase));
            });
        }
        catch (OperationCanceledException) { StatusText.Text = "Создание инстанса отменено"; DetailText.Text = "Незавершённую папку можно удалить вручную."; }
        catch (Exception ex) { StatusText.Text = "Не удалось создать инстанс"; DetailText.Text = ex.Message; RefreshInstances(); }
        finally { _downloadCts?.Dispose(); _downloadCts = null; }
    }

    private UIElement BuildPackSearchPage(string source, out TextBox query, out TextBlock status, out ListBox results, out Button search, out Button install, out Button configureKey)
    {
        var page = new Grid { Margin = new Thickness(22) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 3, 0, 15) };
        heading.Children.Add(new TextBlock { Text = source + " сборки", FontSize = 25, FontWeight = FontWeights.Bold, Foreground = AccentBrush });
        heading.Children.Add(new TextBlock { Text = "Найди modpack и установи его отдельным инстансом.", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), Margin = new Thickness(0, 4, 0, 0) });
        page.Children.Add(heading);
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        query = new TextBox { MinHeight = 38, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Название сборки" };
        search = new Button { Content = "Искать", MinWidth = 92, Background = AccentBrush, Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = AccentBrush };
        configureKey = new Button { Content = "API key", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Visibility = source == "CurseForge" ? Visibility.Visible : Visibility.Collapsed };
        toolbar.Children.Add(query); Grid.SetColumn(search, 1); toolbar.Children.Add(search); Grid.SetColumn(configureKey, 2); toolbar.Children.Add(configureKey);
        Grid.SetRow(toolbar, 1); page.Children.Add(toolbar);
        results = new ListBox { Background = new SolidColorBrush(Color.FromRgb(25, 28, 26)), BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 50)), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
        var template = new DataTemplate(typeof(ModProject));
        var card = new FrameworkElementFactory(typeof(StackPanel)); card.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 6, 8, 6)); card.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(ModProject.Avatar))); image.SetValue(FrameworkElement.WidthProperty, 38d); image.SetValue(FrameworkElement.HeightProperty, 38d); image.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0)); card.AppendChild(image);
        var textColumn = new FrameworkElementFactory(typeof(StackPanel)); textColumn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ModProject.Title))); title.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold); title.SetValue(TextBlock.FontSizeProperty, 14d); textColumn.AppendChild(title);
        var description = new FrameworkElementFactory(typeof(TextBlock)); description.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ModProject.Description))); description.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); description.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(147, 155, 149))); description.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0)); textColumn.AppendChild(description);
        card.AppendChild(textColumn);
        template.VisualTree = card; results.ItemTemplate = template; Grid.SetRow(results, 2); page.Children.Add(results);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status = new TextBlock { Text = "Нажми «Искать» или введи название сборки.", Foreground = new SolidColorBrush(Color.FromRgb(147, 155, 149)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        install = new Button { Content = "Установить выбранную сборку", Background = AccentBrush, Foreground = new SolidColorBrush(Color.FromRgb(19, 23, 14)), BorderBrush = AccentBrush, Margin = new Thickness(10, 0, 0, 0) };
        footer.Children.Add(status); Grid.SetColumn(install, 1); footer.Children.Add(install); Grid.SetRow(footer, 3); page.Children.Add(footer);
        return page;
    }

    private async Task InstallVersionAsync(GameVersion version, string instanceDir, IProgress<(int Percent, string Message)> progress, CancellationToken ct, bool installingLoader)
    {
        var versionDir = Path.Combine(instanceDir, "versions", version.Id);
        Directory.CreateDirectory(versionDir);
        progress.Report((1, "Получаю метаданные версии…"));
        using var metadataResponse = await Http.GetAsync(version.Url, ct);
        metadataResponse.EnsureSuccessStatusCode();
        var metadataBytes = await metadataResponse.Content.ReadAsByteArrayAsync(ct);
        var metadataPath = Path.Combine(versionDir, version.Id + ".json");
        await File.WriteAllBytesAsync(metadataPath, metadataBytes, ct);
        using var metadata = JsonDocument.Parse(metadataBytes);
        var root = metadata.RootElement;
        var tasks = new List<(string Url, string Path, string? Sha1)>();

        if (root.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var client))
            tasks.Add((client.GetProperty("url").GetString()!, Path.Combine(versionDir, version.Id + ".jar"), OptionalString(client, "sha1")));
        else throw new InvalidDataException("В метаданных версии отсутствует клиентский JAR.");

        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateArray())
            {
                if (!IsAllowedOnWindows(lib)) continue;
                if (lib.TryGetProperty("downloads", out var libDownloads))
                {
                    if (libDownloads.TryGetProperty("artifact", out var artifact)) AddArtifact(artifact, tasks);
                    if (libDownloads.TryGetProperty("classifiers", out var classifiers) && lib.TryGetProperty("natives", out var natives) && natives.TryGetProperty("windows", out var winNative))
                    {
                        var classifierName = winNative.GetString()?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                        if (classifierName is not null && classifiers.TryGetProperty(classifierName, out var nativeArtifact)) AddArtifact(nativeArtifact, tasks);
                    }
                }
                else if (lib.TryGetProperty("name", out var name))
                {
                    AddMavenLibrary(lib, tasks);
                }
            }
        }

        string? assetIndexId = null;
        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            assetIndexId = assetIndex.GetProperty("id").GetString();
            var indexPath = Path.Combine(_root, "assets", "indexes", assetIndexId + ".json");
            tasks.Add((assetIndex.GetProperty("url").GetString()!, indexPath, OptionalString(assetIndex, "sha1")));
        }

        progress.Report((4, $"Подготовлено файлов: {tasks.Count} · проверяю уже загруженные…"));
        await DownloadBatchAsync(tasks, 4, assetIndexId is null ? 81 : 51, "Загрузка файлов", progress, ct);

        if (assetIndexId is not null)
        {
            var indexPath = Path.Combine(_root, "assets", "indexes", assetIndexId + ".json");
            using var assetDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(indexPath, ct));
            var objects = assetDoc.RootElement.GetProperty("objects").EnumerateObject().Select(p => p.Value.GetProperty("hash").GetString()!).Distinct().ToList();
            var assetTasks = objects.Select(hash =>
            {
                var rel = hash[..2] + "/" + hash;
                return ("https://resources.download.minecraft.net/" + rel, Path.Combine(_root, "assets", "objects", hash[..2], hash), (string?)hash);
            }).ToList();
            await DownloadBatchAsync(assetTasks, 55, 30, "Загрузка assets", progress, ct);
        }
        await File.WriteAllTextAsync(Path.Combine(versionDir, ".installed"), DateTimeOffset.UtcNow.ToString("O"), ct);
        progress.Report((installingLoader ? 88 : 100, installingLoader ? "База Minecraft готова" : "Установка завершена"));
    }

    private async Task<string> InstallLoaderProfileAsync(string gameVersion, string loader, string instanceDir, IProgress<(int Percent, string Message)> progress, CancellationToken ct)
    {
        if (loader is "Forge" or "NeoForge")
            return await InstallForgeFamilyLoaderAsync(gameVersion, loader, instanceDir, progress, ct);

        var fabric = loader == "Fabric";
        var api = fabric ? "https://meta.fabricmc.net/v2" : "https://meta.quiltmc.org/v3";
        var versionsUrl = $"{api}/versions/loader/{Uri.EscapeDataString(gameVersion)}";
        progress.Report((90, $"Ищу совместимый {loader}…"));
        using var versionsResponse = await Http.GetAsync(versionsUrl, ct);
        versionsResponse.EnsureSuccessStatusCode();
        using var versionsDoc = JsonDocument.Parse(await versionsResponse.Content.ReadAsStringAsync(ct));
        var loaderVersions = versionsDoc.RootElement.EnumerateArray().ToArray();
        if (loaderVersions.Length == 0) throw new InvalidDataException($"{loader} пока не поддерживает Minecraft {gameVersion}.");
        string? loaderVersion = null;
        foreach (var candidate in loaderVersions)
        {
            var data = candidate.TryGetProperty("loader", out var loaderData) ? loaderData : candidate;
            var stable = data.TryGetProperty("stable", out var stableValue) && stableValue.GetBoolean();
            if (loaderVersion is null || stable) loaderVersion = data.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
            if (stable) break;
        }
        if (string.IsNullOrWhiteSpace(loaderVersion)) throw new InvalidDataException($"API {loader} не вернул номер версии загрузчика.");

        var profileUrl = $"{api}/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        using var profileResponse = await Http.GetAsync(profileUrl, ct);
        profileResponse.EnsureSuccessStatusCode();
        var profileBytes = await profileResponse.Content.ReadAsByteArrayAsync(ct);
        using var profileDoc = JsonDocument.Parse(profileBytes);
        var profile = profileDoc.RootElement;
        var profileId = profile.GetProperty("id").GetString() ?? throw new InvalidDataException("В профиле загрузчика нет id.");
        var profileDir = Path.Combine(instanceDir, "versions", profileId);
        Directory.CreateDirectory(profileDir);
        await File.WriteAllBytesAsync(Path.Combine(profileDir, profileId + ".json"), profileBytes, ct);

        var tasks = new List<(string Url, string Path, string? Sha1)>();
        if (profile.TryGetProperty("libraries", out var libraries))
            foreach (var library in libraries.EnumerateArray())
            {
                if (!IsAllowedOnWindows(library)) continue;
                if (library.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("artifact", out var artifact)) AddArtifact(artifact, tasks);
                else if (library.TryGetProperty("name", out _)) AddMavenLibrary(library, tasks);
            }
        progress.Report((92, $"Скачиваю библиотеки {loader}…"));
        await DownloadBatchAsync(tasks, 92, 8, $"{loader}: библиотеки", progress, ct);
        return loaderVersion;
    }

    private async Task<string> InstallForgeFamilyLoaderAsync(string gameVersion, string loader, string instanceDir, IProgress<(int Percent, string Message)> progress, CancellationToken ct)
    {
        progress.Report((89, L($"Ищу совместимую версию {loader}…", $"Finding a compatible {loader} version…")));
        string build;
        string installerUrl;
        if (loader == "Forge")
        {
            using var metadataResponse = await Http.GetAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
            metadataResponse.EnsureSuccessStatusCode();
            using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(ct));
            var promotions = metadata.RootElement.GetProperty("promos");
            string? promoted = null;
            foreach (var suffix in new[] { "recommended", "latest" })
                if (promotions.TryGetProperty(gameVersion + "-" + suffix, out var value)) { promoted = value.GetString(); if (promoted is not null) break; }
            if (string.IsNullOrWhiteSpace(promoted)) throw new InvalidDataException(L($"Forge не выпустил установщик для Minecraft {gameVersion}.", $"Forge has no installer for Minecraft {gameVersion}."));
            build = promoted;
            installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{gameVersion}-{build}/forge-{gameVersion}-{build}-installer.jar";
        }
        else
        {
            using var metadataResponse = await Http.GetAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct);
            metadataResponse.EnsureSuccessStatusCode();
            using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(ct));
            var neoGameVersion = ToNeoForgeGameVersion(gameVersion);
            build = metadata.RootElement.EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(version => version.StartsWith(neoGameVersion + ".", StringComparison.Ordinal) && Version.TryParse(version, out _))
                .OrderByDescending(version => Version.Parse(version))
                .FirstOrDefault() ?? throw new InvalidDataException(L($"NeoForge не выпустил установщик для Minecraft {gameVersion}.", $"NeoForge has no installer for Minecraft {gameVersion}."));
            installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{build}/neoforge-{build}-installer.jar";
        }

        var installerPath = Path.Combine(_root, "downloads", $"{loader.ToLowerInvariant()}-{gameVersion}-{build}-installer.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
        if (!File.Exists(installerPath))
        {
            var temporaryInstaller = installerPath + ".download";
            try
            {
                using var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using (var target = new FileStream(temporaryInstaller, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;
                        var percent = total > 0 ? 89 + (int)(5 * received / total) : -1;
                        progress.Report((percent, L($"Скачиваю установщик {loader}…", $"Downloading {loader} installer…")));
                    }
                }
                File.Move(temporaryInstaller, installerPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryInstaller)) File.Delete(temporaryInstaller); }
        }

        var javaMajor = GuessJavaMajor(gameVersion);
        var java = await FindJavaAsync(javaMajor);
        progress.Report((95, L($"Устанавливаю {loader} в инстанс…", $"Installing {loader} into the instance…")));
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(java)
            {
                WorkingDirectory = instanceDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.StartInfo.ArgumentList.Add("-jar");
            process.StartInfo.ArgumentList.Add(installerPath);
            process.StartInfo.ArgumentList.Add("--installClient");
            process.StartInfo.ArgumentList.Add(instanceDir);
            if (!process.Start()) throw new InvalidOperationException(L($"Не удалось запустить установщик {loader}.", $"Could not start the {loader} installer."));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw; }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(L($"Установщик {loader} завершился с ошибкой: ", $"The {loader} installer failed: ") + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
        }

        var versionsDir = Path.Combine(instanceDir, "versions");
        var profileJson = Directory.Exists(versionsDir)
            ? Directory.EnumerateFiles(versionsDir, "*.json", SearchOption.AllDirectories).FirstOrDefault(file =>
            {
                try
                {
                    using var profile = JsonDocument.Parse(File.ReadAllBytes(file));
                    return profile.RootElement.TryGetProperty("inheritsFrom", out var parent) && string.Equals(parent.GetString(), gameVersion, StringComparison.OrdinalIgnoreCase);
                }
                catch (JsonException) { return false; }
            })
            : null;
        if (profileJson is null) throw new InvalidDataException(L($"Установщик {loader} завершился, но профиль игры не появился.", $"The {loader} installer finished but did not create a game profile."));

        var instanceLibraries = Path.Combine(instanceDir, "libraries");
        if (Directory.Exists(instanceLibraries))
        {
            foreach (var source in Directory.EnumerateFiles(instanceLibraries, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(instanceLibraries, source);
                var destination = Path.Combine(_root, "libraries", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
        progress.Report((100, L($"{loader} установлен.", $"{loader} installed.")));
        return build;
    }

    private static string ToNeoForgeGameVersion(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length == 2 && parts[0] == "1") return parts[1] + ".0";
        return parts.Length >= 3 && parts[0] == "1" ? string.Join('.', parts.Skip(1)) : gameVersion;
    }

    private void AddMavenLibrary(JsonElement library, List<(string Url, string Path, string? Sha1)> tasks)
    {
        var coordinate = OptionalString(library, "name");
        if (string.IsNullOrWhiteSpace(coordinate)) return;
        var pieces = coordinate.Split(':');
        if (pieces.Length < 3) return;
        var artifactVersion = pieces[2];
        var extension = "jar";
        var at = artifactVersion.IndexOf('@');
        if (at >= 0) { extension = artifactVersion[(at + 1)..]; artifactVersion = artifactVersion[..at]; }
        var classifier = pieces.Length > 3 ? "-" + pieces[3] : "";
        var relative = string.Join('/', pieces[0].Split('.')) + "/" + pieces[1] + "/" + artifactVersion + "/" + pieces[1] + "-" + artifactVersion + classifier + "." + extension;
        if (relative.Split('/').Any(p => p == "..")) throw new InvalidDataException("Недопустимая координата Maven в метаданных.");
        var baseUrl = OptionalString(library, "url") ?? "https://libraries.minecraft.net/";
        tasks.Add((baseUrl.TrimEnd('/') + "/" + relative, Path.Combine(_root, "libraries", relative.Replace('/', Path.DirectorySeparatorChar)), OptionalString(library, "sha1")));
    }

    private async Task DownloadBatchAsync(IReadOnlyList<(string Url, string Path, string? Sha1)> tasks, int start, int span, string label, IProgress<(int Percent, string Message)> progress, CancellationToken ct)
    {
        int completed = 0;
        int total = tasks.Count;
        using var gate = new SemaphoreSlim(10);
        var workers = tasks.Select(async task =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!await IsValidFileAsync(task.Path, task.Sha1, ct))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(task.Path)!);
                    var temp = task.Path + ".download";
                    try
                    {
                        using var response = await Http.GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                            await response.Content.CopyToAsync(output, ct);
                        if (task.Sha1 is not null && !await IsValidFileAsync(temp, task.Sha1, ct)) throw new InvalidDataException("Контрольная сумма файла не совпала.");
                        File.Move(temp, task.Path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            finally
            {
                gate.Release();
                var count = Interlocked.Increment(ref completed);
                var percent = start + (int)(span * count / Math.Max(1, total));
                progress.Report((percent, $"{label}: {count:N0} / {total:N0}"));
            }
        });
        await Task.WhenAll(workers);
    }

    private void AddArtifact(JsonElement artifact, List<(string Url, string Path, string? Sha1)> tasks)
    {
        if (!artifact.TryGetProperty("path", out var path) || !artifact.TryGetProperty("url", out var url)) return;
        var relative = path.GetString() ?? "";
        if (relative.Split('/', '\\').Any(p => p == "..")) throw new InvalidDataException("Недопустимый путь в метаданных библиотеки.");
        tasks.Add((url.GetString()!, Path.Combine(_root, "libraries", relative.Replace('/', Path.DirectorySeparatorChar)), OptionalString(artifact, "sha1")));
    }

    private static bool IsAllowedOnWindows(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os) && os.TryGetProperty("name", out var osName)) matches = osName.GetString() == "windows";
            if (matches) allowed = rule.GetProperty("action").GetString() == "allow";
        }
        return allowed;
    }

    private static string? OptionalString(JsonElement obj, string prop) => obj.TryGetProperty(prop, out var value) ? value.GetString() : null;
    private static async Task<bool> IsValidFileAsync(string path, string? sha1, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(sha1)) return new FileInfo(path).Length > 0;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
        return actual.Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }
}




