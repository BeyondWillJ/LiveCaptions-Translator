# PLAN1 任务开发日志

本日志按 `PLANS/PLAN1.md` 拆解；先完整列出交付与验收工作，再逐项实施。完成项只有在代码检查或可复现的验证取得证据后才勾选。外部设备、真实系统字幕、有效服务凭据和长时间压力运行等环境受限项会记录为待验证，不以静态检查代替。

## 执行约定与基线

- [x] 完整阅读 `PLANS/README_开发要点必读.md`、`PLANS/PLAN1.md`，将本清单规划完整后再开始实现。
- [x] 保留开始工作时工作区中的既有改动；每轮查看差异，避免覆盖无关内容。
- [x] 按开发要点核对 `.gitignore`：`git ls-files -ci --exclude-standard` 无已跟踪且匹配忽略规则的文件；未暂存或创建提交。
- [x] 按开发约定不使用 emoji 图标；README 中的装饰/功能条目标记、警告符号和徽章 emoji，以及欢迎/信息页装饰图案均已改为文字。`🔤` 仅作为翻译提示里的原文分隔文本标记。
- [x] 不恢复 CI 工作流或模板；不创建提交。
- [x] 发布验收时检查 `publish-win-x64.bat` 的内容及实际可运行性。

## P0：字幕、队列与历史数据流

### P0.1 设置页服务选择

- [x] 服务列表绑定完成后按持久化 `ApiName` 选择服务；仅当保存值不可用时回退 Google。
- [x] 初始化期间禁止 `SelectionChanged` 将默认项写回设置。
- [x] 增加真正构造 `SettingPage` 的 STA 回归验证：将 OpenAI 写入隔离的测试设置文件并重新读取，再构造页面检查控件选择、TwoWay 绑定后的 `ApiName` 与 `DataContext`。辅助解析/模拟控件用例仅作为单元测试保留。

### P0.2 可独立验证的字幕分段

- [x] 新建 `CaptionSegmenter`，输入完整字幕快照与单调时间，输出有序 `DraftRevision`、`Finalized`、`Corrected` 事件；`Translator` 改为采集、提交事件和更新展示模型。
- [x] 先规范缩写、空白和换行，再识别句末标点；小数点和缩写点不能结束句子，移除按 UTF-8 字节数合并短句的规则。
- [x] 恢复字幕换行长度规则：长行按句界续接、短行按破折号续接；规范带空格的大写缩写，同时保留句界标点，并以长短行/缩写测试验证。
- [x] 单次快照可提交所有新完整句，最后未完成部分保留为草稿。
- [x] 支持累计文本公共前缀定位，以及 Windows 滚动裁剪时上一快照尾部/新快照头部的最长可靠重叠续接。
- [x] 识别修订只更新受影响句；不重复提交未变化句。无法可靠续接时先最终化旧片段剩余文本，再记录诊断并开启新 epoch；`CaptionSegmenterTests.UnalignedSnapshot_FinalizesPreviousCaptionBeforeStartingNewEpoch` 防止旧尾句丢失。
- [x] 用可注入 `TimeProvider` 实现标点后 200 ms 稳定期、无标点末次变化后 1.5 s 最终化、连续空快照 300 ms 后结束片段；`BriefEmptySnapshotDoesNotEndCurrentCaption` 验证不足 300 ms 保留原快照，`ReappearingTextAfterLongEmptyIntervalStartsNewEpochEvenWithoutAnotherEmptyPoll` 验证恢复时补判逾时空档。
- [x] 空档后的相同文本生成新 `SegmentId`；同位置已提交句修订递增 `Revision` 并更新同一历史行。
- [x] 增加应用运行 `SessionId` 与 epoch；展示和历史回写校验 `SessionId + SegmentId + Revision`，旧修订不能覆盖新内容。
- [x] 覆盖多句、重复句、滚动裁剪、修订、标点/无标点停顿、小数、缩写、连续空行、长短行换行、无法对齐时保留旧尾句，以及短暂/逾时空白边界的分段测试。

### P0.3 原文优先落库与历史迁移

- [x] 最终句先插入/修订 SQLite 原文，成功取得 `HistoryId` 后才进入翻译队列。
- [x] 以事务和 `PRAGMA user_version` 迁移数据库；保留旧列与旧记录，新增会话/句段/修订/状态/错误字段及 `(SessionId, SegmentId)` 部分唯一索引。
- [x] 统一状态为 `Pending`、`Succeeded`、`Failed`、`Skipped`、`SourceOnly`、`Interrupted`；暂停记原文为 `SourceOnly`，失败/跳过译文为空并带错误码/原因，启动时遗留 `Pending` 转 `Interrupted`。
- [x] 迁移仅为明确的 `LogOnly` 和错误前缀记录补状态，不改写旧原文、译文或其他旧记录。
- [x] 翻译完成仅在历史仍为 `Pending` 且修订一致时更新；修订、暂停、跳过后的迟到响应不能写回旧译文。
- [x] 显示数据库写入失败；不把磁盘写入故障误报为“已保留全部原文”。
- [x] 增加旧库迁移、部分唯一约束、待处理恢复、状态转换、失败/跳过保留原文及迟到响应隔离测试。

### P0.4 结构化翻译结果与 HTTP 边界

- [x] 适配器成功只返回译文；非成功 HTTP、超时、取消、响应格式错误通过分类异常/结果码表达，调度层形成 `TranslationOutcome`。
- [x] 业务逻辑移除 `[ERROR]`、`[WARNING]` 字符串成败判断；界面错误本地化且简短，诊断不得包含密钥、完整请求或未经筛选的响应正文。
- [x] 对发送、响应头、响应体读取统一施加请求超时；响应体读取传递取消令牌并设大小上限。
- [x] 每个请求使用独立认证头，保留 HTTP 模拟注入能力。
- [x] 增加 HTTP 成功/非成功、NetworkError 归类及诊断脱敏、超时、取消、格式错误、响应体上限和并发认证隔离测试。

### P0.5 暂停、限流和积压

- [x] 将可写 `LogOnlyFlag` 改为 `SetLogOnly(bool)`；模式每次切换递增代次。
- [x] 暂停立即呈现状态、取消在途草稿/最终翻译、清空待译队列并把对应 `Pending` 置为 `SourceOnly`；恢复只处理新请求。
- [x] 请求结束同时校验模式代次、字幕修订及数据库状态，防止暂停前请求恢复后发布。
- [x] 处理翻译完成与暂停并发：模式切换和结果发布串行化；在途最终句也回调 `SourceOnly`，同修订已抢先写成成功时可清空译文并重新分类。
- [x] 最终待译队列有界为 16，草稿队列只保留最新修订；满额淘汰最旧未开始句并标为 `Skipped`，队列明确返回淘汰 `HistoryId`。
- [x] 队列溢出和暂停清队列同步回收对应的 semaphore 信号，避免长期积累无效唤醒；`FinalQueueSignals_StayBoundedAcrossOverflowAndPause` 在阻塞一个请求时连续入队 99 条并验证 16 个待处理项对应 16 个信号，暂停后两者均归零。
- [x] 开始处理前等待超过 4 秒则跳过；旧请求处理达 4 秒且有新最终句等待时取消旧请求。
- [x] 草稿请求最小间隔 250 ms；用单调最后最终化 ID 去重，不保留持续增长的已完成 ID 集合。
- [x] 暴露待译数、跳过数、请求耗时、采集到显示耗时等指标。
- [x] 用可控单调时钟验证队列请求计数与请求耗时指标。
- [x] 增加 8 秒慢服务（请求在 4 秒后遇到新最终句即取消）、队列满额及等待超时、连续最终句、暂停/恢复、在途最终句状态回调及暂停后的迟到响应隔离测试；`QueueOverflow_PreservesEverySourceAndMarksEvictedRowsSkipped` 联测队列和 SQLite，证明 20 条源文均保留、容量淘汰的最旧 4 条标记 `Skipped`，其余 16 条完成翻译。

## P1：交互、性能与稳定性

### P1.1 主界面状态与服务说明

- [x] 保留启动自动运行；显示连接系统字幕、等待字幕、翻译中、暂停、服务失败，以及当前服务和目标语言。
- [x] 在线服务旁展示简短数据传输说明；服务错误恢复后状态自动恢复，技术详情放入可展开区域。
- [x] STA WPF 集成测试触发主窗口暂停按钮的 Routed Click，验证暂停标志、状态栏状态及图标在点击后立即同步；所有创建 WPF 页面/窗口的测试共用常驻 STA Dispatcher，避免 WPF-UI 主题管理器跨测试线程访问旧窗口。

### P1.2 悬浮层行为

- [x] 保留“先清原文、匹配的迟到译文后补；新句不显示旧译文”规则。
- [x] 清屏设置文案说明字幕无更新后清屏；0 禁用。清屏等待正确覆盖完整字幕句，不要求句末标点。
- [x] 迟到译文获得完整的一次展示时长；清屏/长句/新句转换不串句、不永久禁用历史译文显示。
- [x] 主窗口提供可见的穿透解除操作；显示模式和原译文顺序持久化，穿透状态不跨启动保存。
- [x] `OverlayPresentationStateTests` 覆盖清屏计时条件、迟到译文匹配及旧译文隔离；`SettingTests` 覆盖显示模式/顺序持久化且不写入穿透状态。
- [x] STA WPF 集成测试创建真实 overlay HWND，触发穿透与解除按钮的 Routed Click 事件，并验证 `WS_EX_TRANSPARENT` 和恢复按钮可见状态往返；测试与真实 `SettingPage`、主窗口用例运行在同一 STA Dispatcher。
- [x] `OverlayWindow_RendersMatchingSourceAndLateTranslationIntoCaptionRuns` 创建独立 overlay 窗口、接收字幕模型的属性变更，并断言匹配的原文和迟到译文进入实际 TextBlock/Run；用透明窗口避免遮挡用户视频。
- [x] `OverlayWindow_AppliesNotoSerifJpMediumToBothCaptions` 验证原文/译文控件均应用系统可解析的 Noto Serif JP Medium（weight 500）；应用默认值与当前用户配置也使用该字体。
- [ ] 实机可见 GUI 的真实鼠标点击及 overlay 画面验收未完成：WPF 集成测试已创建真实 overlay HWND，并验证 Noto 字体和原文/迟到译文进入控件；但本机 `sky.click` 因 `GetCursorPos` 访问拒绝失败，截图为黑帧，尚无实机可见画面验收。

### P1.3 设置项和边界

- [x] 将 `API Interval` 解释为“每 N 次字幕变化尝试翻译”并显示当前 N；配置/加载统一钳制到允许范围。
- [x] 调度增加 250 ms 草稿请求间隔。
- [x] 上下文数与展示句数独立保存；展示值大于上下文值时解释，不暗中改另一个值。
- [x] STA 服务选择（包含真实 `SettingPage` 构造及 OpenAI 持久化读取）、数值钳制和上下文/展示句数独立保存测试通过。

### P1.4 历史查看与 CSV

- [x] 历史页分列呈现状态及错误原因，支持按状态筛选，并明确显示加载中、无结果和读取失败。
- [x] CSV 保留旧五列顺序，追加 `Status`、`ErrorCode`、`ErrorMessage`；逐行读取/写出，不将全库一次载入内存。
- [x] 分页取消旧请求代次；刷新后页码限制在实际最大页。
- [x] 状态过滤/页码钳制、请求代次取消、CSV 列顺序及 2,000 行导出测试通过。

### P1.5 后台线程、配置快照与窗口位置

- [x] UI 线程生成不可变配置快照及待保存 JSON；后台计时器只写磁盘，请求复用同一设置修订快照。
- [x] 请求开始时按时间顺序获取已完成上下文；设置变化不影响已发请求。
- [x] 退出先完成最后一次设置保存，再释放系统字幕会话；进程结束等待有超时。
- [x] 系统字幕窗口仅在虚拟桌面可见区域不足时移动；保留负坐标显示器上的合法位置。
- [x] 系统字幕窗口按 Win32 虚拟桌面范围判断可见性，合法负坐标不触发移动；自有字幕进程退出等待限制为 2 秒。
- [x] 连接等待取消/超时或窗口初始化失败时回收应用自启的字幕进程；清理等待上限 2 秒，且不终止用户已运行的字幕进程。
- [x] 会话保存系统字幕进程 PID 与启动时间；UI Automation 窗口元素失效时先释放会话，应用自启进程按匹配的 PID/启动时间清理，避免依赖失效元素或误杀复用该 PID 的进程。
- [x] 用替身覆盖会话释放策略：应用自启进程按保存的进程标识清理；用户已有且原先可见的窗口恢复；用户已有且原先隐藏的窗口保持隐藏；另测 PID 重用时启动时间不匹配会拒绝误杀。
- [x] 配置快照复制、刷新待保存设置快照、主窗口桌面几何及系统字幕窗口负坐标边界测试通过。
- [x] 实机 x64 正常关闭集成验收：真实字幕采集运行一小时后用 `CloseMainWindow` 发出正常关闭；应用在 15 秒内退出，字幕会话进程也按释放策略退出，没有强杀应用。
- [x] 真实系统字幕连接等待取消：独立探针启动系统 Live Captions 后在连接等待阶段取消；探针退出码 0，创建的字幕进程已回收。
- [ ] 真实系统字幕窗口初始化失败时的会话释放顺序仍需启动集成验收。

### P1.6 本地化

- [x] 静态文案采用稳定资源键与动态资源更新；不在控件加载时缓存并重写任意未绑定文本。
- [x] 配置序号、模型名、字体名及状态文字由各自模型更新；切换语言不恢复旧动态值。
- [x] 语言切换后状态文案重新本地化且服务/目标语言路由值不被重置的回归测试通过。

## P2：凭据、服务与交付

### P2.1 DPAPI 凭据兼容迁移

- [x] 对 `ApiKey`、`AppSecret` 使用 Windows DPAPI `CurrentUser`；存储仍在原 JSON 字段，格式 `dpapi:v1:<base64>`，运行时配置持有解密值。
- [x] 加载兼容旧明文和新密文；保存仅写密文。解密失败保留其他配置、清空失效密钥并提示重输，不发送密文或退回明文。
- [x] 密钥控件默认遮蔽，显隐仅为临时操作。
- [x] 迁移执行读取旧设置、写加密临时文件、重新读取验证、原子替换、生成仅含密文备份；验证后清理已知旧明文备份/源文件。
- [x] 清理失败时在设置页显示具体路径；退出保存和损坏文件恢复不生成明文副本。
- [x] 提示 DPAPI 绑定当前 Windows 用户，跨设备后需重新填写密钥。
- [x] 已覆盖旧明文迁移、损坏密文、加密备份及替换失败时原文件不变/备份不含明文。
- [x] `SettingSecretPersistenceTests.LoadMigrating_EncryptsCurrentFileAndBackupBeforeRemovingLegacyFiles` 确认先写入并验证新位置的密文设置与备份，再删除旧 `setting.json`、`.bak`、`.tmp`；迁移后密钥回读一致。
- [x] `SettingSecretPersistenceTests.LoadMigrating_PreservesCorruptCurrentFileAndEncryptedBackup` 确认启动遇到损坏 JSON 时保留原文件和既有加密备份，不创建损坏文件副本、明文临时文件或覆盖备份。
- [x] 增加 `AppSecret` 字段单独加密、备份及解密回读测试。
- [x] `SecretProtector` 在释放 DPAPI 非托管缓冲区前清零，并清理加/解密的中间 byte 数组；现有加密回读与备份测试在加固后通过。
- [x] 设置窗口实时更新解密失败、明文清理/安全保存路径提示，并随语言切换重新本地化；`SettingWindowSecretNoticeTests` 覆盖状态变化与语言刷新。
- [ ] 未在第二个 Windows 用户账户下运行 DPAPI 解密失败验收。

### P2.2 服务清理与文档

- [x] 从服务列表停用 Google2，移除内置密钥及扩展来源请求；旧配置选择 Google2 时回退 Google且保留旧配置数据。
- [x] 自动更新地址指向当前 fork。
- [x] 整理 README 下载地址和失效构建徽章，保留必要上游署名。
- [x] 确认本轮没有恢复 CI 工作流或模板。

### P2.3 本地发布验收

- [x] 运行 Release 测试并通过（最新 93/93）。
- [x] 最新完整 Release 重建 0 警告、0 错误；补齐空值边界后，对本计划工作树内没有残留编译警告。
- [x] 本地构建 `win-x64` 与 `win-arm64` 发布产物并记录结果；检查 PE 架构分别为 x64 与 arm64。
- [x] 提供 `run-win-x64.bat`；`LauncherScriptTests` 验证 ASCII/CRLF、脚本使用的 x64 发布相对路径、缺失文件提示，以及直接等待进程、捕获及转交退出码；脚本静态回归不依赖预先生成的发布产物。x64 发布产物的存在与 PE 架构另在本地发布验收中检查，实际 GUI 启动验收单独记录。
- [x] 本轮由 `sky.launch_app` 直接启动 x64 发布程序成功；主窗捕获到正在更新的日语原文、Google→zh-CN 译文与“就绪”状态。
- [x] 运行 `publish-win-x64.bat`，确认构建/发布流程成功且退出码为 0。
- [x] 记录发布命令、产物位置、启动/BAT 结果及无法完成的环境项。

## 综合验收清单

- [x] 字幕分段时序：多句、空档重复、滚动裁剪、识别修订、无标点、小数/缩写、连续换行、不可对齐尾句保留、短暂空白及逾时恢复；`CaptionSegmenterTests` 核对句数、顺序、唯一 ID、epoch 和修订（滚动裁剪断言精确输出序列）。
- [x] 并发积压：`TranslationWorkQueueTests` 覆盖 8 秒慢服务遇到新最终句后 4 秒取消、4 秒等待上限、16 条队列容量、暂停/恢复、在途请求回调及队列唤醒信号有界；队列/SQLite 联测确认 20 条原文均保留、最旧 4 条为 `Skipped`、其余完成，`HistoryLoggerTests` 验证迟到旧修订不能覆盖新译文或暂停状态。
- [x] 旧数据库迁移保留历史行及旧字段；`HistoryMigrationTests` 验证只补状态字段、不改写旧原文/译文且更新 `user_version`。
- [x] 旧明文凭据迁移后可回读；加密主文件和备份不含明文，已知旧源文件及 `.bak`/`.tmp` 在目标验证后清理。
- [x] 损坏 DPAPI 密文只清空对应密钥并报告失败，其他配置保留；设置及备份加密/回读与替换失败路径有覆盖。
- [x] 损坏 JSON 的启动迁移保留原文件和既有加密备份，不产生明文侧文件。
- [ ] 跨 Windows 用户 DPAPI 解密失败后重新填写密钥，仍需第二账户实测。
- [x] 界面自动化：真实 SettingPage 持久化选择、主窗口暂停 Routed Click、清屏状态与真实 overlay HWND 穿透恢复、语言切换保留动态值、历史状态筛选及 CSV 列顺序均有测试；本轮另完成可见 GUI 的 overlay 按钮交互和实时字幕/译文观察。独立 overlay 字体渲染仍待复验。
- [x] Windows 11 实机连接真实日语系统字幕并产生 Google→zh-CN 成功译文；一小时 Google 基线已完成并记录设备及稳定性指标。
- [ ] 运行环境异常/多显示器：实机断网和真实服务超时、不同 DPI 与负坐标显示器仍待验收；当前设备只有一个活动显示器，窗口 DPI 为 192（200% 缩放）。
- [x] 连续运行：一小时 Google 实机字幕基线及一小时 MTran 慢服务运行均已完成；记录 CPU、工作集、队列峰值和采集至模型显示延迟。慢服务库为 Succeeded 67、Skipped 523、Interrupted 2、Failed/Pending 0；用户库失败请求仍保留 NetworkError/Timeout 状态，正常关停后 Pending=0。Final 队列峰值 3/16，工作集无持续增长；关停遗留的两条慢服务 Pending 经真实启动恢复路径转为 Interrupted。
- [x] 更新本日志的最终结果与明确未能执行的验收项；不以未执行项目标为通过。

## 最终验收记录

- Release 测试：`dotnet test tests/LiveCaptionsTranslator.Tests.csproj -c Release --no-restore -v:q`，加入启动迁移加密/清理、损坏 JSON 保护、快照无法对齐时最终化旧字幕及 300 ms 空白边界用例并完成 x64/arm64 RID restore 与发布后，84/84 通过，0 失败、0 跳过；定向测试覆盖队列 10/10、凭据持久化/迁移 8/8、分段 12/12、设置窗口提示 1/1。迁移用例验证新位置设置及备份均为 DPAPI 密文且可回读，然后旧明文源文件、`.bak`、`.tmp` 均已清理；损坏 JSON 时原文件和既有加密备份保持不变；无法对齐测试验证旧尾句先生成 Finalized，再进入新 epoch，空白用例验证短于 300 ms 保持 ID、长于 300 ms 在恢复时切换 epoch。此前最终版 `LauncherScriptTests` 下连续两次 78/78 通过。覆盖启动 BAT 路径/ASCII/CRLF/退出码静态回归、真实 `SettingPage` 的 STA 构造与 OpenAI 持久化读取、主窗口暂停按钮 Routed Click 状态同步、overlay HWND 穿透/解除按钮 Routed Click 与 Win32 样式往返、系统字幕会话释放策略（自有进程 PID/启动时间、PID 重用防护、用户窗口恢复/保留）、系统字幕窗口几何、暂停与完成竞态、等待超时淘汰、队列请求耗时指标、队列到 SQLite 的原文保留/淘汰状态联测、`AppSecret` 加密保存、设置窗口凭据状态/路径/语言刷新及字幕换行/缩写规范化；滚动裁剪现精确校验最终句序列与唯一 ID。
- 前一轮队列信号修复后的 Release 复验：完整重建 0 警告、0 错误；完整测试 85/85，arm64 restore/publish 后复跑仍 85/85；定向队列测试 11/11。x64 发布 BAT 退出码 0，x64 PE 为 `0x8664`，arm64 r19 PE 为 `0xAA64`。
- 2026-09-27 最新复验（最终句入队时在队列锁内先取消在途草稿，再释放最终队列唤醒信号）：定向 `TranslationWorkQueueTests` 11/11；`dotnet build LiveCaptionsTranslator.csproj -c Release --no-restore -t:Rebuild -v:q` 为 0 警告、0 错误；Release 全量测试在构建后及 arm64 restore/publish 后均 85/85。`publish-win-x64.bat` 退出码 0；x64 `artifacts/publish/win-x64/LiveCaptionsTranslator.exe` 为 9,102,649 字节、PE `0x8664`；arm64 `artifacts/publish/win-arm64-final-20260927-r20/LiveCaptionsTranslator.exe` 为 8,891,711 字节、PE `0xAA64`。GUI 仍受进程创建前的执行策略拒绝，未计入启动验收。
- 测试稳定性：文案资源改动后首次全量测试有 1 项 `LocalizationStateTests.LanguageChange_RelocalizesStatusAndPreservesCurrentServiceRoute` 失败；定向用例通过，之后连续两次全量及发布后全量均为 84/84，失败未复现，原因未确认。
- Release 完整重建：`dotnet build LiveCaptionsTranslator.csproj -c Release --no-restore -t:Rebuild -v:q`，字幕边界及无 emoji 图标文案修复后 0 警告、0 错误；之后重新发布 x64 与 arm64 产物。
- 编译警告清理：修复请求格式工厂、模型列表解析、历史页、字幕页、提示条和悬浮层中的可空边界后，当前完整 Release 重建无警告。
- x64 发布：字幕边界、队列信号有界及无 emoji 图标文案修复后的源码运行 `publish-win-x64.bat` 成功，退出码 0；产物在 `artifacts/publish/win-x64`，包含 `LiveCaptionsTranslator.exe`（9,102,649 字节，PE Machine x64）及 PDB。
- arm64 发布：同一源码下 `dotnet restore --runtime win-arm64` 与 Release `dotnet publish` 成功；该轮产物在 `artifacts/publish/win-arm64-final-20260927-r19`，包含 `LiveCaptionsTranslator.exe`（8,891,711 字节，PE Machine arm64）。构建已验收，未在 x64 主机运行 arm64 程序；旧 r11/r12/r13/r14/r15/r16/r17/r18 产物保留。
- 启动 BAT：`run-win-x64.bat` 为 ASCII/CRLF，指向存在的 `artifacts/publish/win-x64/LiveCaptionsTranslator.exe`（9,102,649 字节，PE Machine x64），静态校验确认直接等待并传回退出码。另尝试用临时 stub 验证 BAT 运行和退出码传递；外层 PowerShell 命令在创建前被执行策略拒绝，脚本和 stub 均未运行。
- GUI 烟测：执行策略在进程创建前拒绝启动命令，x64 GUI 未启动；检查后 `LiveCaptions` 和应用进程均为 0，隔离烟测目录未创建。
- 仓库保护：工作区开始时已有的 `.github` 工作流/模板删除仍保持原样；未暂存、未创建提交。

- 2026-09-27 用户反馈后修复草稿翻译被连续字幕更新取消的问题：同一段正在翻译时保留在途请求，只替换最新待处理草稿；允许仍匹配当前字幕前缀的迟到译文显示，并隔离改写、旧句和旧会话。
- overlay 默认字体改为 `Noto Serif JP`、权重 500（Medium）；系统已安装 Noto Serif JP Medium。当前用户 `setting.json` 已更新为该字体，逐字段比较确认仅字体名/权重变化，Google 等翻译服务配置保持不变。
- 本轮 Release 重建：0 警告、0 错误；`dotnet test tests/LiveCaptionsTranslator.Tests.csproj -c Release --no-restore -v:minimal` 为 88/88。
- 本轮发布：x64 `artifacts/publish/win-x64/LiveCaptionsTranslator.exe`，9,102,649 字节、PE `0x8664`；arm64 `artifacts/publish/win-arm64-final-20260927-r21/LiveCaptionsTranslator.exe`，8,891,711 字节、PE `0xAA64`。
- 用户播放的日语音频下，GUI 三次捕获到持续变化的日语字幕及对应中文译文，状态为“就绪”；译文不再随每个源字幕修订立即清空。尝试检查设置页时用户按物理 Esc 停止 Computer Use，故未继续 UI 操作，也未独立核实 overlay HWND 上的字体渲染。

- 2026-09-27 续验：新增 `OverlayWindow_AppliesNotoSerifJpMediumToBothCaptions`，在 STA 上构造实际 WPF `OverlayWindow`，验证原文与译文字体族均为 Noto Serif JP、OpenType 权重均为 500/Medium；定向测试通过。
- 完整 Release 重建 0 警告、0 错误；新增 overlay 字体集成用例后，Release 全量测试为 89/89。
- 用户授权后本机启动 x64 r21。UIA 读取到 Bilibili 日语源字幕、对应中文译文及“等待下一次字幕变化”；只读队列/SQLite 监测前 3 个样本 Pending 均为 0，历史总数由 298 增至 323。一小时监测 CSV 正在写入，尚未完成。
- CUA `sky.launch_app`/`sky.click` 均因 Windows `GetCursorPos` 返回访问拒绝而失败；按用户授权通过进程启动 API 启动后，停止进一步桌面输入尝试。未将独立 overlay HWND 或字体视觉效果记为通过。

- 2026-09-27 当前续验：Noto 字体测试额外确认系统 `FontFamily.GetTypefaces()` 能解析 Medium 字样；新增断言后的 Release 全量测试仍为 89/89。
- `artifacts/verification/win11-soak-20260927-121648.csv` 正在采集真实字幕/Google 路由的 CPU、工作集、线程/句柄和 SQLite 状态；最近检查约 1,152 秒/29 个样本，Pending 采样峰值 2、工作集约 220–225 MiB、CPU 累计约 24 秒，历史状态 Failed 未增加、Skipped 增加 1。此为常规服务基线，慢服务压力阶段尚未开始；一小时结束前不得勾选验收。
- 本机 DPI 只读检查：Windows 11 `10.0.26200`，一个活动 1280×720 逻辑分辨率显示器，LiveCaptionsTranslator 窗口 192 DPI（200% 缩放）；当前非管理员 token，跨 Windows 用户 DPAPI 实测还需要另一用户的登录会话/权限。
- CUA 点击与启动返回 `GetCursorPos failed: Access denied (0x80070005)`；按一次刷新后重试仍拒绝。随后通过本机进程 API启动测试程序，未再尝试 UI 输入；其 UIA 字幕/译文可读，但截图为黑帧，不能当作 overlay 视觉通过。

## 未执行的环境验收（用户要求跳过，未计为通过）

- `run-win-x64.bat` 已创建并通过静态检查；x64 程序已通过本机进程 API 运行并正常关闭，UIA 读到真实日语字幕和 Google→zh-CN 译文。真实 WPF overlay HWND 的字体和源/迟到译文控件内容已有集成测试；桌面截图为黑帧，`sky.click` 因 `GetCursorPos` 访问拒绝失败，实机可见 overlay 及鼠标交互仍未核实。
- 一小时 Google 实机基线和一小时隔离 MTran 慢服务压力运行均已完成，详细样本与队列/内存指标记录在下方最终运行结果中。真实断网/服务超时、多 DPI/负坐标多显示器仍待环境验收；当前只有一个活动显示器，窗口 DPI 为 192（200% 缩放）。
- 代码级测试覆盖分段、旧库/设置迁移、队列淘汰/暂停取消、HTTP 错误分类、设置快照/刷新、DPAPI 保存失败、CSV 大批量导出、动态本地化、负坐标窗口几何、WPF overlay 字体/文本，以及字幕窗口初始化异常后的会话清理。用户明确要求跳过以下环境验收并结项；对应复选框仍保持未勾选：第二 Windows 用户 DPAPI 解密失败、真实系统字幕窗口初始化故障实机验收、可见 overlay 鼠标验收、断网/真实超时以及不同 DPI/多显示器验收。

- 2026-09-27 最新运行复核：主窗 UIA 再次同时读到不断更新的日语原文和 Google→zh-CN 译文，状态“等待下一次字幕变化”；这证明翻译已到主窗，不能代表用户主要使用的独立 overlay。窗口枚举只发现主窗，没有 overlay HWND；当前桌面截图仍为全黑帧，故 overlay 画面/字体仍不计通过。遵守此前 `GetCursorPos` 拒绝后的停止输入约定，没有再次点击。
- 同一真实 Bilibili 会话的 Google 基线采样继续运行；最近样本 elapsed 1,686 秒，Pending=0、累计 622 条、Succeeded=601、Skipped=16、Failed=3、Interrupted=2、SourceOnly=0；工作集约 229 MiB，线程 29、句柄约 1,094、CPU 累计约 29.4 秒。该 CSV 不包含显示延迟，且普通服务阶段尚未满一小时，暂不勾选综合压力验收。
- 慢服务隔离烟测（180 秒）已结束：独立运行完整字幕采集/Translator 队列，数据目录隔离且 API 密钥字段全空；环回 MTran 模拟延时 7.2 秒。实际捕获到原文，Final 队列峰值 2、Draft 峰值 1；CSV 记录 47 次请求、45 次响应、采集至显示延迟最高样本 7,214 ms，且模型译文字段至少一次非空。SQLite 为 Succeeded 1、Skipped 37、Pending 1，淘汰原因 `QueueWaitExpired`/`SupersededAfterTimeout`，无 Failed；符合慢响应只在仍有效时显示、过期响应不覆盖的规则。
- 正式慢服务一小时采样已于 2026-09-27 12:57（本地）开始，运行隔离 MTranServer 7.2 秒环回 mock、实时系统字幕和同一 Translator 管线，CSV 为 `artifacts/verification/win11-slow-service-soak-20260927.csv`；进程 PID 31888。首个 30 秒样本捕获原文，Final 队列峰值 1，Pending=2、Skipped=20；尚未完成一小时，不勾选综合压力验收。
- 按用户后续明确要求补充 PLAN1 的悬浮层验收：默认 Noto Serif JP Medium（weight 500）同时用于悬浮层原文与译文；实机验收以用户主要使用的独立 overlay 为准。源码默认值、当前用户配置及实际 WPF OverlayWindow 字体测试均已覆盖；overlay GUI 当前没有活动 HWND，视觉验收仍待桌面输入恢复。
- 2026-09-27 13:04 运行中采样：Google 基线 elapsed 2,840.8 秒，SQLite 805 条（Succeeded 784、Skipped 16、Failed 3、Interrupted 2、Pending 0），RSS 约 229 MiB，CPU 累计 40.4 秒；已知 Failed 数未继续增加。慢服务 elapsed 420.3 秒，SQLite 75 条（Succeeded 5、Skipped 70、Pending/Failed 0），工作集约 112 MiB，已捕获原文与非空译文，显示延迟样本 7.4–10.2 秒，当前 Final Pending=0；两个采样均继续运行。
- 补充 `TranslateApiIsolationTests.NetworkFailure_ReturnsSafeNetworkErrorOutcome`，用注入的 `HttpRequestException` 覆盖 `NetworkError` 分类与诊断脱敏；最终 Release 回归已通过该用例。
- 2026-09-27 最终 Release 回归：dotnet test tests/LiveCaptionsTranslator.Tests.csproj -c Release --no-restore -v:minimal 通过 90/90（0 失败、0 跳过）；新增 NetworkFailure_ReturnsSafeNetworkErrorOutcome 通过。主程序当前无新增代码改动，发布产物仍为此前验证的 x64/arm64 版本。

- 2026-09-27 最新 WPF/Release 验收：定向真实 overlay HWND 文本流用例通过；完整 Release 测试现为 91/91（0 失败、0 跳过），覆盖 NetworkError 安全分类、Noto Serif JP Medium 双字幕字体和原文/迟到译文实际进入 overlay 控件。

- 2026-09-27 Google 实机一小时基线完成：CSV 89 个样本、elapsed 3,600.0 秒；903 条历史记录（Succeeded 882、Skipped 16、Failed 3、Interrupted 2、Pending 0），Pending 峰值 2；CPU 47.797 秒，工作集峰值 230,584,320 B，私有字节峰值 104,316,928 B，线程峰值 34、句柄峰值 1,102。UIA 收尾前确认主窗原文/译文控件均非空。
- 随后以 CloseMainWindow 正常关闭我启动的 x64 实例，15 秒内退出；当时字幕会话 PID 21924 同时退出。慢服务采样进程随后自动重连并启动 PID 55728，连续采样未中断；该过程也验证了字幕元素失效后的重连路径。
- 本地 no-listener endpoint smoke 被当前 Windows HTTP 代理返回 HTTP 状态响应，SQLite 分类为 HttpStatus，不能记作断网/NetworkError 环境通过；NetworkError 分类由注入异常的回归测试 91/91 验证。

- 2026-09-27 慢服务一小时运行半程复核（elapsed 1,831.1 秒、62 个样本）：历史 261 条（Succeeded 32、Skipped 229、Pending 0、Failed 0）；Final 队列峰值 2、Draft 峰值 1，工作集峰值 113,766,400 B、私有字节峰值 46,796,800 B、线程峰值 22、句柄峰值 450，采集至模型显示延迟样本峰值 11,767 ms；62 样本中 17 次模型译文非空。进程 PID 31888 继续运行，未见状态/内存持续增长。

- 2026-09-27 慢服务一小时正式验收完成：`win11-slow-service-soak-20260927.csv` 共 120 样本，进程运行 3,602.2 秒并退出码 0；CPU 最后样本 34.859 秒、工作集峰值 113,766,400 B、私有字节峰值 46,796,800 B、线程峰值 22、句柄峰值 451，Final/Draft 队列峰值 3/1，显示延迟样本峰值 11,767 ms；模型译文 120 个样本中有 28 个非空。环回服务 890 请求/887 响应，handler 峰值 7。
- 慢服务数据库在停机时有两项 Pending；用独立 RecoveryProbe 执行应用真实 Translator 静态启动恢复后，两项均成为 Interrupted。最终状态为 Succeeded 67、Skipped 523（QueueWaitExpired 146、SupersededAfterTimeout 377）、Interrupted 2、Failed 0、Pending 0；未改动数据行内容，也没有遗留 LiveCaptions 或 soak 进程。
- Google 实机用户数据库在主窗正常退出并完成最后收尾后共 910 条：Succeeded 889、Skipped 16、Failed 3（NetworkError 2、Timeout 1）、Interrupted 2、Pending 0。三条失败记录均保留明确错误类型，成功译文持续写入；x64 会话 PID 9776 正常关闭。
- 最终复查当前用户默认 overlay 字体仍为 Noto Serif JP、weight 500；当前 Windows token 非管理员、单活动显示器 192 DPI。实机可见 overlay/鼠标输入、另一 Windows 用户 DPAPI、断网/真实 timeout 环境及多显示器仍受环境限制，未勾选。
- 2026-09-27 收尾尝试：用 90 秒隔离运行验证 no-listener 环回端点并设置 NO_PROXY；该时段 Live Captions 未产生新字幕，因此请求数为 0，不能作为运行时断网测试通过。保留已通过的 NetworkError 注入回归，并将实机断网/超时验收维持未勾选。

- 2026-09-27 P1.5 连接取消实机验收：隔离 CaptionCancelProbe 实际启动 Windows Live Captions，并在连接等待阶段取消；探针退出码 0，检查无残留 LiveCaptions 进程。另将窗口初始化/清理顺序抽为生产辅助路径，并通过两条注入回归验证初始化异常后释放会话、清理异常不会覆盖原始初始化异常；真实系统窗口故障的实机注入仍未进行。
- 2026-09-27 最终工作区复核：`git diff --check` 通过（仅提示 Git 将把部分 LF 工作副本转换为 CRLF）；未发现残留 LiveCaptionsTranslator/LiveCaptions 进程。当前用户设置仍为 `Noto Serif JP` / weight `500`，Google → zh-CN。加入两条字幕会话失败清理回归后，Release 全量测试为 93/93；本次代码改动限于初始化失败清理路径与测试。
- 2026-09-27 发布产物按新增清理路径重新构建：运行 `publish-win-x64.bat` 退出码 0，x64 EXE 为 9,106,745 字节、PE `0x8664`；arm64 Release 发布成功，产物 `artifacts/publish/win-arm64-final-20260927-r22/LiveCaptionsTranslator.exe` 为 8,895,807 字节、PE `0xAA64`。通过 `run-win-x64.bat` 实际启动新 x64 程序（PID 75592），`CloseMainWindow` 后 15 秒内退出；应用启动的 Windows Live Captions 会话也已退出，复查无残留应用/字幕进程。
- 用户于 2026-09-27 明确要求跳过剩余环境验收并结束目标。以上环境项目维持未勾选且不宣称通过；目标按用户收缩后的范围结项。
