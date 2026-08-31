using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator.utils
{
    public static class LocalizationService
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Translations =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["zh-CN"] = new(StringComparer.Ordinal),
                ["ja-JP"] = new(StringComparer.Ordinal),
                ["zh-TW"] = new(StringComparer.Ordinal)
            };
        private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Originals = new();
        private static readonly Dictionary<string, string> CanonicalSources = new(StringComparer.Ordinal);
        private static bool initialized;

        public static string CurrentLanguage { get; private set; } = "zh-CN";

        static LocalizationService()
        {
            Add("Caption", "字幕", "字幕", "字幕");
            Add("Setting", "设置", "設定", "設定");
            Add("History", "历史", "履歴", "歷史");
            Add("Info", "信息", "情報", "資訊");
            Add("Log Cards of Captions", "字幕记录卡片", "字幕ログカード", "字幕記錄卡片");
            Add("Pause Translation (Log Only)", "暂停翻译（仅记录）", "翻訳を一時停止（記録のみ）", "暫停翻譯（僅記錄）");
            Add("Overlay Window", "悬浮字幕窗口", "オーバーレイ字幕", "懸浮字幕視窗");
            Add("Always on Top", "始终置顶", "常に手前に表示", "永遠置頂");
            Add("Font Size Increase", "增大字体", "フォントを大きく", "放大字體");
            Add("Font Size Decrease", "减小字体", "フォントを小さく", "縮小字體");
            Add("Font Stroke Increase", "加粗描边", "縁取りを太く", "加粗描邊");
            Add("Font Stroke Decrease", "减细描边", "縁取りを細く", "減細描邊");
            Add("Font Bold", "字体加粗", "太字", "字體加粗");
            Add("Font Color", "字体颜色", "文字色", "字體顏色");
            Add("Background Opacity Increase", "提高背景不透明度", "背景を濃く", "提高背景不透明度");
            Add("Background Opacity Decrease", "降低背景不透明度", "背景を薄く", "降低背景不透明度");
            Add("Background Color", "背景颜色", "背景色", "背景顏色");
            Add("Show only subtitles or translations", "仅显示原文或译文", "原文または翻訳のみ表示", "僅顯示原文或譯文");
            Add("Switch the order of subtitles and translations", "切换原文与译文顺序", "原文と翻訳の順序を切り替え", "切換原文與譯文順序");
            Add("Click Through", "鼠标穿透", "クリック透過", "滑鼠穿透");
            Add("LiveCaptions", "实时字幕", "ライブ キャプション", "即時字幕");
            Add("Show", "显示", "表示", "顯示");
            Add("Hide", "隐藏", "非表示", "隱藏");
            Add("API Interval", "API 调用间隔", "API 呼び出し間隔", "API 呼叫間隔");
            Add("Translate API", "翻译 API", "翻訳 API", "翻譯 API");
            Add("Target Language", "目标语言", "翻訳先言語", "目標語言");
            Add("API Setting", "API 设置", "API 設定", "API 設定");
            Add("Open", "打开", "開く", "開啟");
            Add("Show Latency", "显示延迟", "遅延を表示", "顯示延遲");
            Add("Off", "关", "オフ", "關");
            Add("On", "开", "オン", "開");
            Add("Contexts", "上下文数量", "コンテキスト数", "上下文數量");
            Add("Display Sentences", "Overlay 显示句数", "オーバーレイ表示文数", "Overlay 顯示句數");
            Add("Context Aware", "上下文感知", "文脈を考慮", "上下文感知");
            Add("Overlay Font", "悬浮字幕字体", "オーバーレイのフォント", "懸浮字幕字體");
            Add("Clear After Silence (s)", "静默后清屏（秒）", "無音後に消去（秒）", "靜默後清屏（秒）");
            Add("0 disables automatic Overlay clearing", "设为 0 可关闭悬浮字幕自动清屏", "0 にすると自動消去を無効化します", "設為 0 可關閉懸浮字幕自動清屏");
            Add("UI Language", "界面语言", "表示言語", "介面語言");
            Add("Except for Google and Google2, all other APIs require configuring before they can be used.",
                "除 Google 和 Google2 外，其他 API 均需配置后才能使用。",
                "Google と Google2 以外の API は、使用前に設定が必要です。",
                "除 Google 和 Google2 外，其他 API 均需設定後才能使用。");
            Add("Determines the frequency of translate API calls. The smaller it is, the more frequent API calls.",
                "控制翻译 API 的调用频率，数值越小调用越频繁。",
                "翻訳 API の呼び出し頻度です。小さいほど頻繁に呼び出します。",
                "控制翻譯 API 的呼叫頻率，數值越小呼叫越頻繁。");
            Add("Translate in context.", "结合上下文翻译。", "文脈を考慮して翻訳します。", "結合上下文翻譯。");
            Add("\nIt can improve translation accuracy, but will consume more tokens.",
                "\n可提高翻译准确度，但会消耗更多 Token。",
                "\n翻訳精度は向上しますが、より多くのトークンを消費します。",
                "\n可提高翻譯準確度，但會消耗更多 Token。");
            Add("Search", "搜索", "検索", "搜尋");
            Add("Previous ", "上一页", "前のページ", "上一頁");
            Add("Next Page", "下一页", "次のページ", "下一頁");
            Add("Export", "导出", "エクスポート", "匯出");
            Add("Delete All", "全部删除", "すべて削除", "全部刪除");
            Add("Refresh", "刷新", "更新", "重新整理");
            Add("Time", "时间", "日時", "時間");
            Add("Translated", "译文", "翻訳", "譯文");
            Add("Click To Copy", "点击复制", "クリックしてコピー", "點擊複製");
            Add("Click to Copy, Ctrl+Scroll to Resize Font", "点击复制，Ctrl+滚轮调整字号", "クリックでコピー、Ctrl+ホイールで文字サイズ変更", "點擊複製，Ctrl+滾輪調整字號");
            Add("Getting Started", "开始使用", "はじめに", "開始使用");
            Add("Welcome to LiveCaptions Translator!", "欢迎使用 LiveCaptions Translator！", "LiveCaptions Translator へようこそ！", "歡迎使用 LiveCaptions Translator！");
            Add("LiveCaptions Translator is based on Windows LiveCaptions.",
                "LiveCaptions Translator 基于 Windows 实时字幕。",
                "LiveCaptions Translator は Windows ライブ キャプションを利用します。",
                "LiveCaptions Translator 基於 Windows 即時字幕。");
            Add("I have fully understood and configured.", "我已了解并完成配置", "内容を理解し、設定しました", "我已瞭解並完成設定");
            Add("Before your first running, you need to configure it according to the following steps:\n",
                "首次运行前，请按以下步骤配置：\n", "初回起動前に、次の手順で設定してください：\n", "首次執行前，請按以下步驟設定：\n");
            Add("\n1. Open Windows LiveCaptions and follow the guide to download the necessary files for on-device speech recognition and language packs.",
                "\n1. 打开 Windows 实时字幕，按提示下载本地语音识别文件和语言包。",
                "\n1. Windows ライブ キャプションを開き、案内に従って音声認識ファイルと言語パックをダウンロードします。",
                "\n1. 開啟 Windows 即時字幕，依提示下載本機語音辨識檔案和語言套件。");
            Add("\nFor more details, visit our wiki: ", "\n更多说明请访问 Wiki：", "\n詳しくは Wiki をご覧ください：", "\n更多說明請造訪 Wiki：");
            Add("\n\nTip:", "\n\n提示：", "\n\nヒント：", "\n\n提示：");
            Add("Source Language", "源语言", "音声言語", "來源語言");
            Add("Repository:", "代码仓库：", "リポジトリ：", "程式碼倉庫：");
            Add("Wiki:", "Wiki：", "Wiki：", "Wiki：");
            Add("Maintainer:", "维护者：", "メンテナー：", "維護者：");
            Add("Version:", "版本：", "バージョン：", "版本：");
            Add("We welcome any form of contribution! You can provide suggestions or report bugs via GitHub issues, or contribute code directly by submitting Pull Requests.",
                "欢迎任何形式的贡献！你可以通过 GitHub Issue 提建议或报告问题，也可以提交 Pull Request 直接贡献代码。",
                "あらゆる貢献を歓迎します。GitHub Issues で提案や不具合を報告したり、Pull Request でコードを提供できます。",
                "歡迎任何形式的貢獻！你可以透過 GitHub Issue 提建議或回報問題，也可以提交 Pull Request 直接貢獻程式碼。");
            Add("Prompt", "提示词", "プロンプト", "提示詞");
            Add("Current Config: ", "当前配置：", "現在の設定：", "目前設定：");
            Add("New", "新建", "新規", "新增");
            Add("Delete", "删除", "削除", "刪除");
            Add("You must keep at least one config.", "必须至少保留一个配置。", "設定を少なくとも1つ残す必要があります。", "必須至少保留一個設定。");
            Add("Model Name", "模型名称", "モデル名", "模型名稱");
            Add("Temperature", "温度", "Temperature", "溫度");
            Add("API Url", "API 地址", "API URL", "API 網址");
            Add("API Url (Base)", "API 基础地址", "API ベース URL", "API 基礎網址");
            Add("API Key", "API 密钥", "API キー", "API 金鑰");
            Add("APP ID", "应用 ID", "アプリ ID", "應用程式 ID");
            Add("APP Key", "应用密钥", "アプリキー", "應用程式金鑰");
            Add("APP Secret", "应用密钥 Secret", "アプリシークレット", "應用程式 Secret");
            Add("Load Models", "加载模型", "モデルを読み込む", "載入模型");
            Add("No need to explicitly add", "无需手动添加", "明示的に追加する必要はありません", "無需手動加入");
            Add("suffix.", "后缀。", "サフィックス。", "後綴。");
            Add("Note 1:", "注意 1：", "注意 1：", "注意 1：");
            Add("\nNote 2:", "\n注意 2：", "\n注意 2：", "\n注意 2：");
            Add("\n\nNote:", "\n\n注意：", "\n\n注意：", "\n\n注意：");
            Add("After Windows 11 version 24H2, you can only change the", "Windows 11 24H2 及更高版本只能在实时字幕中更改", "Windows 11 24H2 以降では、ライブ キャプション内でのみ変更できます：", "Windows 11 24H2 及更高版本只能在即時字幕中變更");
            Add("in LiveCaptions.", "。", "。", "。");
            Add("Please click", "请点击", "次をクリックしてください：", "請點擊");
            Add("\"Hide\"", "“隐藏”", "「非表示」", "「隱藏」");
            Add("to hide LiveCaptions instead of closing it directly.", "来隐藏实时字幕，请勿直接关闭。", "ライブ キャプションを直接閉じずに非表示にします。", "來隱藏即時字幕，請勿直接關閉。");
            Add("\nThe translate API is called once after the caption changes", "\n字幕内容发生变化后，每经过", "\n字幕が変化してから", "\n字幕內容發生變化後，每經過");
            Add("[API Interval]", "[API 调用间隔]", "[API 呼び出し間隔]", "[API 呼叫間隔]");
            Add("times.", "次变化调用一次翻译 API。", "回の変化ごとに翻訳 API を呼び出します。", "次變化呼叫一次翻譯 API。");
            Add("\"There isn’t the target language I expect!\"", "“没有我需要的目标语言！”", "「希望する翻訳先言語がない！」", "「沒有我需要的目標語言！」");
            Add("\nYou can directly edit the content of this combobox to customize the language, and it is recommended to follow the", "\n可以直接编辑此下拉框自定义语言，建议遵循", "\nこのコンボボックスを直接編集して言語を指定できます。次の形式を推奨します：", "\n可以直接編輯此下拉框自訂語言，建議遵循");
            Add("BCP 47 language tag.", "BCP 47 语言标签。", "BCP 47 言語タグ。", "BCP 47 語言標籤。");
            Add("Some of APIs (such as DeepL) needs another way to define target language, see their official docs for more details.", "部分 API（如 DeepL）使用不同的目标语言格式，详情请查看其官方文档。", "一部の API（DeepL など）は翻訳先言語の指定方法が異なります。公式ドキュメントをご確認ください。", "部分 API（如 DeepL）使用不同的目標語言格式，詳情請查看其官方文件。");
            Add("\nNo need to consider this for included target languages, since we've built in tag mappings. But if your expected language isn't in the list, keep this in mind.", "\n内置目标语言已做好标签映射；仅在自定义列表外语言时需要注意。", "\n内蔵言語にはタグ変換が用意されています。リスト外の言語を指定する場合のみご注意ください。", "\n內建目標語言已做好標籤對應；僅在自訂清單外語言時需要注意。");
            Add("Contexts:", "上下文：", "コンテキスト：", "上下文：");
            Add("Determines the number of context sentences when", "设置启用", "次の機能を有効にしたときの文脈文数：", "設定啟用");
            Add("is enabled.", "时使用的上下文句数。", "。", "時使用的上下文句數。");
            Add("\nOverlay Sentences:", "\nOverlay 显示句数：", "\nオーバーレイ表示文数：", "\nOverlay 顯示句數：");
            Add("Determines the number of displayed cards when", "设置启用", "次の機能を有効にしたときのカード表示数：", "設定啟用");
            Add("Log Cards", "记录卡片", "ログカード", "記錄卡片");
            Add("is enabled, as well as the max number of sentences displayed in the Overlay Window.", "时的卡片数量，也是 Overlay 最多显示的句数。", "。オーバーレイに表示する最大文数にも使用します。", "時的卡片數量，也是 Overlay 最多顯示的句數。");
            Add("Contexts must be", "上下文数量必须", "コンテキスト数は表示文数", "上下文數量必須");
            Add("greater than or equal", "大于或等于", "以上にしてください。", "大於或等於");
            Add("Display Sentences. If not met, the program will automatically adjust them.", "显示句数，否则程序会自动调整。", "条件を満たさない場合は自動調整されます。", "顯示句數，否則程式會自動調整。");
            Add("The {0} in the prompt indicates the target language, so make sure your prompt includes {0}.", "提示词中的 {0} 表示目标语言，请确保提示词包含 {0}。", "プロンプト内の {0} は翻訳先言語を表すため、必ず {0} を含めてください。", "提示詞中的 {0} 表示目標語言，請確保提示詞包含 {0}。");
            Add("The source text is enclosed with 🔤.", "源文本会由 🔤 包围。", "原文は 🔤 で囲まれます。", "來源文字會由 🔤 包圍。");
            Add("Base URL ending with", "基础 URL 结尾为", "末尾が次のベース URL：", "基礎 URL 結尾為");
            Add("Use Full Url (typically ending with", "使用完整 URL（通常结尾为", "完全な URL を使用（通常の末尾：", "使用完整 URL（通常結尾為");
            Add(". Chat endpoint and models are appended automatically.", "。聊天端点和模型路径会自动追加。", "。チャットのエンドポイントとモデルは自動追加されます。", "。聊天端點和模型路徑會自動加入。");
            Add("\nIf this program is helpful to you, please consider giving us a star ✨!", "\n如果本程序对你有帮助，欢迎点亮 Star ✨！", "\n役に立った場合は、ぜひ Star ✨ をお願いします！", "\n如果本程式對你有幫助，歡迎點亮 Star ✨！");
            Add("(Author) and", "（作者）以及", "（作者）と", "（作者）以及");
            Add("\n2. Click the", "\n2. 点击 Windows 实时字幕中的", "\n2. Windows ライブ キャプションの", "\n2. 點擊 Windows 即時字幕中的");
            Add("icon in Windows LiveCaptions to open the settings menu, and select", "图标打开设置菜单，然后选择", "アイコンをクリックして設定メニューを開き、次を選択します：", "圖示開啟設定選單，然後選擇");
            Add("\"Position > Overlaid on screen\"", "“位置 > 屏幕叠加”", "「位置 > 画面上にオーバーレイ」", "「位置 > 螢幕疊加」");
            Add(".\n3. Click the", "。\n3. 点击本程序设置页中的", "。\n3. 本アプリの設定ページにある", "。\n3. 點擊本程式設定頁中的");
            Add("button located in the setting page of LiveCaptions Translator to hide Windows LiveCaptions.\n", "按钮以隐藏 Windows 实时字幕。\n", "ボタンをクリックして Windows ライブ キャプションを非表示にします。\n", "按鈕以隱藏 Windows 即時字幕。\n");
            Add("\nThe program has automatically navigated to the setting page and opened Windows LiveCaptions.", "\n程序已自动进入设置页并打开 Windows 实时字幕。", "\n設定ページに移動し、Windows ライブ キャプションを自動的に開きました。", "\n程式已自動進入設定頁並開啟 Windows 即時字幕。");
            Add("After completing the configuration, you can switch back to the main page and enjoy real-time audio translation.\n", "完成配置后即可返回主页面使用实时语音翻译。\n", "設定後はメインページに戻り、リアルタイム音声翻訳を利用できます。\n", "完成設定後即可返回主頁面使用即時語音翻譯。\n");
            Add("To change the", "如需更改", "変更するには：", "如需變更");
            Add(", please [Show] LiveCaptions on the setting page and do it in Windows LiveCaptions.", "，请在设置页[显示]实时字幕，并在 Windows 实时字幕中操作。", "、設定ページでライブ キャプションを［表示］し、Windows 側で変更してください。", "，請在設定頁[顯示]即時字幕，並在 Windows 即時字幕中操作。");
            Add("Do you want to delete all history?", "确定删除全部历史记录吗？", "すべての履歴を削除しますか？", "確定刪除全部歷史記錄嗎？");
            Add("This operation cannot be undone!", "此操作无法撤销！", "この操作は元に戻せません。", "此操作無法復原！");
            Add("Yes", "是", "はい", "是");
            Add("No", "否", "いいえ", "否");
            Add("Saved Success.", "保存成功。", "保存しました。", "儲存成功。");
            Add("Save Failed.", "保存失败。", "保存に失敗しました。", "儲存失敗。");
            Add("File saved to:", "文件已保存至：", "保存先：", "檔案已儲存至：");
            Add("Failed to save file:", "文件保存失败：", "ファイルの保存に失敗しました：", "檔案儲存失敗：");
            Add("[ERROR] Update Check Failed.", "[错误] 检查更新失败。", "[エラー] 更新の確認に失敗しました。", "[錯誤] 檢查更新失敗。");
            Add("[ERROR] Open Browser Failed.", "[错误] 打开浏览器失败。", "[エラー] ブラウザーを開けませんでした。", "[錯誤] 開啟瀏覽器失敗。");
            Add("New Version Available", "有新版本可用", "新しいバージョンがあります", "有新版本可用");
            Add("A new version has been detected:", "检测到新版本：", "新しいバージョン：", "偵測到新版本：");
            Add("Current version:", "当前版本：", "現在のバージョン：", "目前版本：");
            Add("Please visit GitHub to download the latest release.", "请前往 GitHub 下载最新版本。", "GitHub から最新版をダウンロードしてください。", "請前往 GitHub 下載最新版本。");
            Add("Update", "更新", "更新", "更新");
            Add("Ignore this version", "忽略此版本", "このバージョンを無視", "忽略此版本");
            Add("CaptionPage", "字幕", "字幕", "字幕");
            Add("SettingPage", "设置", "設定", "設定");
            Add("HistoryPage", "历史", "履歴", "歷史");
            Add("InfoPage", "信息", "情報", "資訊");
            Add("Previous", "上一页", "前のページ", "上一頁");
            Add("Please set the API URL first.", "请先设置 API 地址。", "先に API URL を設定してください。", "請先設定 API 網址。");
            Add("Loaded {0} model(s).", "已加载 {0} 个模型。", "{0} 個のモデルを読み込みました。", "已載入 {0} 個模型。");
            Add("No models found or unable to connect. Check that the server is running.", "未找到模型或无法连接，请确认服务器正在运行。", "モデルが見つからないか接続できません。サーバーが起動していることを確認してください。", "未找到模型或無法連線，請確認伺服器正在執行。");
            Add("Copied.", "已复制。", "コピーしました。", "已複製。");
            Add("Copy Failed.", "复制失败。", "コピーに失敗しました。", "複製失敗。");
            Add("[Paused]", "[已暂停]", "[一時停止]", "[已暫停]");
            Add("[WARNING] LiveCaptions was unexpectedly closed, restarting...", "[警告] 实时字幕意外关闭，正在重新启动……", "[警告] ライブ キャプションが予期せず終了しました。再起動しています…", "[警告] 即時字幕意外關閉，正在重新啟動……");
            Add("[ERROR] Logging history failed.", "[错误] 写入历史记录失败。", "[エラー] 履歴の記録に失敗しました。", "[錯誤] 寫入歷史記錄失敗。");
            Add(") instead of Base Url (typically ending with just", "），而不是基础 URL（通常仅以", "）を使用し、ベース URL（通常の末尾：", "），而不是基礎 URL（通常僅以");
            Add("⚙️ gear", "⚙️ 齿轮", "⚙️ 歯車", "⚙️ 齒輪");

            foreach (var language in Translations.Values)
                foreach (var pair in language)
                    CanonicalSources.TryAdd(pair.Value, pair.Key);
        }

        public static void Initialize(string language)
        {
            if (!initialized)
            {
                EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(Element_Loaded));
                initialized = true;
            }
            SetLanguage(language, save: false);
        }

        public static void SetLanguage(string language, bool save = true)
        {
            string normalized = Translations.ContainsKey(language) || language == "en-US" ? language : "zh-CN";
            CurrentLanguage = normalized;
            CultureInfo culture = CultureInfo.GetCultureInfo(CurrentLanguage);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            if (save && Translator.Setting != null && Translator.Setting.UiLanguage != CurrentLanguage)
                Translator.Setting.UiLanguage = CurrentLanguage;

            if (Application.Current == null)
                return;
            foreach (Window window in Application.Current.Windows)
                LocalizeTree(window);
        }

        public static string Get(string source)
        {
            if (CurrentLanguage == "en-US")
                return source;
            return Translations[CurrentLanguage].TryGetValue(source, out string? translated) ? translated : source;
        }

        private static void Add(string source, string simplifiedChinese, string japanese, string traditionalChinese)
        {
            Translations["zh-CN"][source] = simplifiedChinese;
            Translations["ja-JP"][source] = japanese;
            Translations["zh-TW"][source] = traditionalChinese;
        }

        public static void Refresh(DependencyObject root) => LocalizeTree(root);

        public static string Format(string source, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, Get(source), args);

        private static void Element_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                LocalizeElement(element);
        }

        private static void LocalizeTree(DependencyObject root)
        {
            var visited = new HashSet<DependencyObject>();
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DependencyObject current = pending.Pop();
                if (!visited.Add(current))
                    continue;
                LocalizeElement(current);

                foreach (object child in LogicalTreeHelper.GetChildren(current))
                    if (child is DependencyObject dependencyChild)
                        pending.Push(dependencyChild);
                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                        pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        private static void LocalizeElement(DependencyObject element)
        {
            if (element is Window window)
                Translate(window, "Title", Window.TitleProperty, () => window.Title, value => window.Title = value);
            if (element is System.Windows.Controls.TextBlock textBlock)
            {
                if (!BindingOperations.IsDataBound(textBlock, System.Windows.Controls.TextBlock.TextProperty))
                {
                    if (textBlock.Inlines.Count > 0)
                    {
                        foreach (Run run in textBlock.Inlines.OfType<Run>().ToList())
                            Translate(run, "Text", Run.TextProperty, () => run.Text, value => run.Text = value);
                    }
                    else
                    {
                        Translate(textBlock, "Text", System.Windows.Controls.TextBlock.TextProperty,
                            () => textBlock.Text, value => textBlock.Text = value);
                    }
                }
            }
            if (element is ContentControl contentControl && contentControl.Content is string)
                Translate(contentControl, "Content", ContentControl.ContentProperty,
                    () => (string)contentControl.Content, value => contentControl.Content = value);
            if (element is HeaderedContentControl headered && headered.Header is string)
                Translate(headered, "Header", HeaderedContentControl.HeaderProperty,
                    () => (string)headered.Header, value => headered.Header = value);
            if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string)
                Translate(frameworkElement, "ToolTip", FrameworkElement.ToolTipProperty,
                    () => (string)frameworkElement.ToolTip, value => frameworkElement.ToolTip = value);
            if (element is AutoSuggestBox autoSuggestBox)
                Translate(autoSuggestBox, "PlaceholderText", AutoSuggestBox.PlaceholderTextProperty,
                    () => autoSuggestBox.PlaceholderText, value => autoSuggestBox.PlaceholderText = value);
            if (element is ToggleSwitch toggleSwitch)
            {
                if (toggleSwitch.OnContent is string)
                    Translate(toggleSwitch, "OnContent", ToggleSwitch.OnContentProperty,
                        () => (string)toggleSwitch.OnContent, value => toggleSwitch.OnContent = value);
                if (toggleSwitch.OffContent is string)
                    Translate(toggleSwitch, "OffContent", ToggleSwitch.OffContentProperty,
                        () => (string)toggleSwitch.OffContent, value => toggleSwitch.OffContent = value);
            }
            if (element is System.Windows.Controls.DataGrid dataGrid)
            {
                foreach (DataGridColumn column in dataGrid.Columns.Where(column => column.Header is string))
                {
                    var originals = Originals.GetOrCreateValue(column);
                    if (!originals.TryGetValue("Header", out string? source))
                    {
                        source = Canonicalize((string)column.Header);
                        originals["Header"] = source;
                    }
                    column.Header = Get(source);
                }
            }

            if (element is Window or Page)
            {
                if (element is FrameworkElement root)
                {
                    root.Language = XmlLanguage.GetLanguage(CurrentLanguage);
                    root.SetValue(TextElement.FontFamilyProperty, new FontFamily(CurrentLanguage switch
                    {
                        "ja-JP" => "Yu Gothic UI",
                        "zh-TW" => "Microsoft JhengHei UI",
                        "zh-CN" => "Microsoft YaHei UI",
                        _ => "Segoe UI"
                    }));
                }
            }
        }

        private static void Translate(DependencyObject owner, string propertyName, DependencyProperty property,
            Func<string> getter, Action<string> setter)
        {
            if (BindingOperations.IsDataBound(owner, property))
                return;
            var originals = Originals.GetOrCreateValue(owner);
            if (!originals.TryGetValue(propertyName, out string? source))
            {
                source = Canonicalize(getter());
                originals[propertyName] = source;
            }
            setter(Get(source));
        }

        private static string Canonicalize(string value) =>
            CanonicalSources.TryGetValue(value, out string? source) ? source : value;
    }
}
