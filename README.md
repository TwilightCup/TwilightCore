# TwilightCore

黄昏杯（Twilight Cup）比赛的**选手端** BepInEx 插件，用于《人类一败涂地》(Human: Fall Flat)。
内嵌 [LevelCollections](https://github.com/...) 合集引擎，并连接已存在的
`TwilightCupBackend` 服务端（FastAPI + WebSocket），实现：聊天、`!ready`、阶段/倒计时、
服务端下发合集自动开跑、准备阶段锁定（防提前起跑）、多关回合的 **subsegment 实时时间差追踪**，
以及一套**模拟计时器**让比赛流程不依赖真实计时器也能跑通到计分/判定。

> 真实计时器（规则精确的每关计时 + 上报）会作为独立模块后续合并；当前用 `SimulatedTimer`
> 临时替代，接口（`IRoundReporter`）已留好，合并时只换实现。

## 安装 / 构建

```bash
# 默认路径指向 macOS Steam 安装；其它机器用 -p 覆盖
dotnet build -c Release \
  -p:GAME_MANAGED="/path/Human_Data/Managed" \
  -p:BEPINEX_CORE="/path/BepInEx/core"
cp bin/Release/netstandard2.0/TwilightCore.dll "<game>/BepInEx/plugins/"
```

若之前装过独立 `LevelCollections.dll`，请移走（TwilightCore 已内置，否则会出现两个合集按钮）。

## 配置

首次启动后在 `BepInEx/config/TwilightCore.cfg` 生成。**后端地址/端口不在配置里**，用 `twi connect <host> [port]` 在控制台传入（默认端口 **8443**，对应公网 nginx HTTPS 入口）。nginx 同源反代：`/api/...`→后端 REST（去掉 `/api` 前缀），`/ws/{token}`→后端 WebSocket，`/`→前端。所以插件登录走 `https://<host>:8443/api/auth/login`，WS 走 `wss://<host>:8443/ws/{token}`：

| 段.键 | 默认 | 说明 |
|---|---|---|
| `Server.UseTLS` | `true` | 走 `wss`/`https`（公网 nginx 走 TLS；host/port 由 `twi connect` 传入）。本地裸服务端时改 `false` |

> **TLS 实现说明**：游戏 Unity 2017.4 自带 Mono 的 TLS 栈太旧，无法与服务端完成 TLS1.2 握手（报 “The authentication or decryption has failed”）。因此 WebSocket 的 TLS 握手改用内嵌的 **BouncyCastle**（纯托管 TLS1.2，自带现代密码套件）。`BouncyCastle.Crypto.dll` 作为独立文件放在 `BepInEx/plugins/` 旁，部署插件时需一并放入。REST 登录走 UnityWebRequest，不受此影响。
| `Account.Username` | _(空)_ | 选手账号用户名 |
| `Account.Password` | _(空)_ | 选手账号口令（明文存储，仅用于换 JWT） |
| `Account.Seat` | _(空)_ | `PLAYER_A`/`PLAYER_B`；留空由服务端按会话指派自动解析 |
| `Net.HeartbeatSecs` | `20` | 心跳间隔 |
| `Net.ReconnectMinBackoffSecs` / `Max` | `1` / `30` | 断线指数退避（重连沿用上次 `twi connect` 的地址） |
| `Features.EnableReadyLock` | `true` | 准备阶段 `!ready` 之后、以及倒计时阶段锁定手动进关（未 ready 前可自由练习） |
| `Features.EnableSimTimer` | `true` | 启用模拟计时器上报 |
| `Features.SameLevelReloadMinDwell` | `1` | 合集连续进同一关时绕道 `Empty` 场景的最短停留秒数（视觉上区分两次尝试）；`0` 关闭绕道 |
| `Features.EnableMenuFallLimit` | `true` | 连接比赛服期间限制主菜单小人下落速度（防坠落） |
| `Features.EnableScenePreload` | `true` | **held-scene 预载**：`!ready` 后把选图（MULTI）首关以休眠方式驻留内存，`round_start` 瞬间换入（见下文「关卡预载」） |
| `Features.EnableChainedPreload` | `true` | **链式预载（M3）**：合集进行中后台预载下一关，过关时帧级换入（见下文「关卡预载」）。若游玩中掉帧明显可关闭（退化为仅首关瞬发）；本地 `lc` 合集同样生效 |
| `Features.EnableProbeFreeze` | `true` | **染色修复（仅烘焙探针的关，如 Halloween/Steam）**：预载保持期内把激活光照探针系数冻结为玩家处采样值（均匀场），换入时写回该关真实系数。未烘焙探针的关（其余内置关）无法安全写入（托管读数与渲染器路径缩放约定不一致），保持期内维持轻微的下一关染色（换关即恢复，属已接受的取舍） |
| `Features.ProbeFreezeUnbakedHolds` | `true` | **未烘焙关保持期全黑修复（实验性）**：hold 非烘焙探针的关（Halloween/Steam 以外全部）时，玩家落在其探针凸包内会让所有动态物体（含玩家模型）采到全零系数、完全失去环境光。此项把冻结同样应用到未烘焙组——值取加载前游玩关结构的实采样，换入**不写回**（场景激活时引擎自会重填，避免历史上的过曝级联）。可能存在轻微亮度偏移；关闭则保持期维持全黑 |
| `Features.PreloadUnloadUnusedAfterSwap` | `true` | **换入后清扫**：每次换入完成、旧场景卸载后执行 `Resources.UnloadUnusedAssets()` 并等待（约 50-145ms）。additive 预载路径会按不同场景累积泄漏 native 资产，长合集（约 12 个不同场景起）最终在场景加载时 OOM 硬崩；清扫即修复（代价是每次换入多一次短卡顿），关闭可做 A/B 对比 |
| `Features.EnableDiskCacheWarmup` | `true` | **保守预载（磁盘缓存预热）**：PREP 期首关 hold 完成后，单一后台线程把合集后续关卡的磁盘文件预读进 OS 页缓存——比赛中的链式预载读盘变内存读，仅剩反序列化 CPU 负担。纯文件 IO（不触碰场景/光照、单块固定缓冲不增进程内存），任何失败静默退化为现状；`twi reload` 热切（关闭只停新预热，进行中的跑完） |
| `Features.DiskWarmupMode` | `follow-chain` | 预热策略：`follow-chain` = PREP 只头暖首关后两关，之后每个链式 hold 完成即预热"下下关"（页面更鲜活、稳态占用小、本地 `lc` 练习局同样生效；局内有温和后台读，落在磁盘空闲窗口）；`prep-all` = PREP 一次性暖全部后续关卡（局内零磁盘 IO）。`twi reload` 热切 |
| `Features.DiskWarmupWarmSharedFiles` | `true` | 磁盘缓存预热时连同共享资产一并预热：按目标关 build index **邻接裁剪**的 `sharedassets{N}.*` 三件套 + `resources.assets`（全合合约 600MB 量级，而非全部 shared 文件的 ~4.1GB；场景表兜底/`warm all` 时退化为全量）；逐文件体积见 `Debug.DebugPreloadLogger` 日志，机器内存吃紧可关（页缓存本就由内核管理、可回收） |
| `Features.EnableSubsegment` | `true` | 多关回合 subsegment 实时时间差追踪（见下节；需服务端已升级 + TwilightTimer 真实计时器在运行） |
| `Subsegment.PlaneRadius` | `50` | 检测平面半径（米）：对方采样点处垂直于其运动向量的虚拟平面范围 |
| `Subsegment.MinMove` | `0.5` | 采样间隔位移小于该值（米）则该样本不生成检测平面（近乎静止） |
| `Subsegment.SampleInterval` | `1` | 采样间隔（秒） |
| `Chat.PopupEnabled` | `true` | 收到消息时弹出仅日志的聊天框（无输入框），随后淡出 |
| `Chat.PopupSecs` | `5` | 上述弹出框持续秒数（之后淡出） |
| `Chat.ToggleHotkey` | `Ctrl+T` | 打开/关闭聊天控制台的快捷键，格式 `修饰键+主键`，如 `Ctrl+T`、`Ctrl+Shift+Y`、`Alt+F8`、`F8`。`Ctrl` 在 macOS 上同时匹配 `Cmd`。可用 `twi reload` 热生效（无需重启） |
| `HUD.Enabled` | `true` | 合集运行期间在屏幕右上角显示两行合集信息（样式仿计时器：粗体纯文本、单色无渐变） |
| `HUD.TextColor` | `FFD94C` | HUD 文本颜色，hex 编码（`RRGGBB` 或 `RRGGBBAA`，可带 `#`） |
| `HUD.FontSize` | `18` | HUD 字号（与 TwilightTimer 计时器默认一致） |
| `Debug.VerboseNetLog` | `false` | 打印每条收发帧 |
| `Debug.DebugPreloadLogger` | `false` | 预载详细诊断日志：逐 hold/换入的光照探针、光照贴图表签名、内存快照、清扫耗时等状态转储（排查光照/内存问题用）。纯日志开关，行为完全一致；警告与错误不受影响，`twi preload rs` 始终可用。可用 `twi reload` 热切换 |
| `Debug.DebugSubsegmentLogger` | `false` | subsegment 详细诊断日志：跟踪器状态迁移、苏醒检测、采样/接收样本、平面创建与穿越检测、命中/完成同步、空转原因等。纯日志开关，行为完全一致；普通 subsegment 状态行不受影响。可用 `twi reload` 热切换 |

## 控制台命令

游戏内按 `` ` ``（BackQuote）或 `F1` 打开控制台。

**合集（移植自 LevelCollections）**

| 命令 | 说明 |
|---|---|
| `lc random [秒]` | 从本地配置池随机抽合集开跑（练习用） |
| `lc restart [秒]` | 从第一关重跑当前合集 |
| `lc skip [秒]` | 跳过当前关（单关=本次尝试记 N/A） |
| `lc abort` | 取消挂起的延迟命令 |

**TwilightCore**

| 命令 | 说明 |
|---|---|
| `twi connect <host> [port]` | 连接（地址在命令里给，默认端口 8443，默认走 `wss`/`https`） |
| `twi disconnect` | 主动断开（停止自动重连） |
| `twi disconnect simulate` | 模拟意外断连：连接会按配置自动重连，用于验证断线重连续传 |
| `twi status` | 连接 / 比赛状态 |
| `twi reload` | 从磁盘热重载 `TwilightCore.cfg` + `LevelCollections.json`（改完配置文件不用重启游戏） |
| `twi sim level_done [ms]` | 模拟当前关/尝试完成（可指定用时） |
| `twi sim skip` | 模拟跳过（N/A） |
| `twi sim complete [final_ms]` | 模拟整回合完成 |
| `twi sim forfeit [multi_exit\|single_exit_0_valid]` | 模拟弃权 |
| `twi sim status` | 模拟计时器状态 |
| `twi subseg status` | subsegment 追踪器状态（回合/关卡/采样与平面数/最近时间差） |
| `twi preload hold <levelId>` | **M1/M3 验证**：把指定关卡以 additive+休眠方式驻留（不经比赛流程）；主菜单或游玩中的关卡内均可（后者即链式预载形态）。id 同合集配置：内置关用显示名（`Aztec`、`Steam`…，区分大小写）或已订阅的 workshop id |
| `twi preload swap` | **M1/M3 验证**：把驻留场景换入为当前关（GO 换入序列；勿在 ready 锁定中使用） |
| `twi preload drop` | 丢弃驻留场景并卸载 |
| `twi preload status` | 预载器状态（选图/驻留场景/失败原因） |
| `twi preload rs` | 转储当前全局光照状态（探针/环境光/雾/光照贴图表逐条纹理标识/逐场景 renderer lightmapIndex 直方图/LOD 层状态/进程内存），排查换入光照与内存问题用 |
| `twi preload mach` | 转储当前关全部机器的关节/物理状态（AngularJoint/Lever/Catapult/铰链角度与驱动目标等），机器异常时当场运行 |
| `twi preload warm <levelId\|all>` | **保守预载验证**：手动把指定关卡（或 `all` = 全部场景文件 + 共享文件）预读进 OS 页缓存，不经比赛流程（调试与冷/热缓存 A/B 用）；进度见 `twi preload status` 的 `warm:` 行 |

聊天：
- **Ctrl+T**（可由 `Chat.ToggleHotkey` 配置，如 `Ctrl+Shift+Y`、`F8` 等）打开/关闭完整聊天控制台（菜单和局内都可用），输入文本回车发送；`!ready`、`!roll` 等就是普通聊天文本。控制台打开时会接管键盘，游戏不会响应抓取/跳跃（移动键仍可能生效，请停步后再打字）。改完快捷键用 `twi reload` 热生效。
- 收到消息时（控制台未打开），会自动弹出**仅显示日志（无输入框）**的聊天框，持续 `Chat.PopupSecs`（默认 5 秒）后淡出；有新消息会顺延。可由 `Chat.PopupEnabled` 关闭。

## Subsegment 实时时间差追踪

多关（MULTI）回合中，双方选手各自跑同一合集，常规上报只有整关完成时间，关内无法比较进度。
启用 `Features.EnableSubsegment` 后：

- **采样**：每个关卡内，从角色**首次从装死状态苏醒**（进关天降落地 → 3 秒无意识 → 起身）
  起，到**该关真正过关**为止，每秒记录一次当前总时间、位置、运动向量并上报服务端。
  注意碰到通关判定区≠过关：游戏流程上要在这之后死亡（坠落或溺水）才触发过关，
  判定区触碰与真正过关之间的走位/坠落耗时**照常计入**（与整关计时同口径）。
  关内手动装死、
  坠崖检查点复活**不会**重置录制（时钟不停，罚时自然计入）。
- **检测**：服务端把一方的采样中转给对方；对方客户端在每个采样点处生成**垂直于运动向量、
  半径 `Subsegment.PlaneRadius` 的虚拟平面**（纯数学检测，不创建任何 Unity 碰撞体，
  不影响关卡物理、不会高速穿隧漏检），本方角色沿运动方向穿越时上报命中时刻。
- **时间差**：服务端记录双方数据（仅内存、回合级，回合结束即清空），每次命中向双方/裁判/
  导播广播 `subsegment_gap`（穿越方时间 − 采样方时间，正数 = 穿越方落后），供导播 overlay 使用。
- **过关强制同步**：真实过关时向对方的**末样本**（其过关时刻的最后一条采样）补发一次命中——
  双方终点必然是同一通关判定区，因此即使路线完全不同、途中一条平面都没碰到，每关也保证
  恰好一次「通关时间差」比较（由后过关的一方发出；若之前真实跨越过该样本，以真实跨越为准）。
- **时间口径**：时间值直接取 **TwilightTimer**（真实计时器，经 `TimerProviderRegistry` 注册）
  的 `RoundTotalMs`——与官方计分**同一条时间线**，时间差可直接与成绩对照。采样窗口为
  每关苏醒 → 真实过关。未注册真实计时器时 subsegment 不工作（**不回退**模拟计时器——
  那是早期调试占位，后续会弃用）。
- 选手侧**无任何游戏内显示**（避免实时看到对方进度干扰心态）；`twi subseg status` 查看本地状态。
- 断线期间样本丢失（可接受）；重连后服务端把对方已存采样按序补放，检测平面自动重建。
- 需要后端同步升级（新消息类型）；连上未升级的服务端时会在首次 400 后自动停发直到下回合。

## 服务端合集配置格式（`collection.raw`）

服务端的 `CollectionConfig` 是不透明字典（`{raw: ...}`），**格式由本插件定义**：

```json
{
  "name": "ML1 - Aztec% + Steam%",
  "levels": ["Aztec", "Steam"]
}
```

- `levels` 为关卡 ID 字符串数组（BuiltIn / EditorPick / Workshop，自动识别）。
- **单关项目（`pick.type=SINGLE`）**：`levels` 放单个关卡，插件按 `pick.retry_count`
  自动重复 N 次（即“自动编排”）。也可手动放多条。
- 多关项目（`MULTI`）：`levels` 即为关卡顺序。

创建比赛/图池时，每个选图的 `collection` 字段按此结构填充，例如：

```json
{
  "code": "ML1", "name": "Aztec% + Steam%", "type": 1, "retry_count": null,
  "collection": { "raw": { "name": "Aztec% + Steam%", "levels": ["Aztec", "Steam"] } }
}
```

## 关卡预载（held-scene，仅 MULTI）

`Features.EnableScenePreload` 开启时，插件会在 PREP 阶段把裁判选定图（**仅 MULTI**）的
**首关场景**提前加载进内存（additive、全部根物体休眠：不渲染/无物理/无音频），
`round_start` 瞬间只做轻量"换入"（激活场景 + 复刻游戏自己的
`AfterLoad` 编排），实现**倒计时结束即在关卡里**。工作方式：

- **触发**：收到服务端 `pick_announced`（裁判选图即提前下发合集）+ 本方 `!ready`，
  且玩家在主菜单。ready-lock 生效后手动进关被锁，预载不会被意外破坏。
- **上报**：预载状态机向服务端报 `preload_report`（`in_progress`/`done`/`failed`/`na`）；
  SINGLE 选图固定报 `na`。服务端据此做开局门控（双方预载完成才自动倒计时）。
- **降级**：预载是优化不是依赖——任何失败（未订阅、下载失败、场景异常、改图）
  自动回退现有标准加载路径，行为与未开预载完全一致；服务端不支持
  `pick_announced` 时插件完全闲置（WS 连接会带 `cap=preload1` 能力参数，旧服务端忽略）。
- **链式预载（`Features.EnableChainedPreload`）**：进入某一关（`PlayingLevel`）后，
  后台以同样的休眠方式预载合集**下一关**（低优先级分帧加载），过关时直接换入——
  关间过渡从数秒加载变为帧级切换，计时器边沿/上报不受影响。相邻同关
  （含 SINGLE 的重复尝试）不预载，走既有 Empty 间隔路径；换关时下一关尚未
  预载完则回退标准加载。本地 `lc` 合集同样生效（无需服务端）。
  保持期染色：烘焙探针的关（如 Halloween/Steam）由 `Features.EnableProbeFreeze`
  修复（保持期内探针系数冻结为玩家处采样值，换入时写回）；未烘焙关由
  `Features.ProbeFreezeUnbakedHolds` 处理（玩家落在下一关探针凸包内时表现为
  动态物体全黑、凸包外为轻微染色，两者同源，冻结后消除；换入不写回）。
  换入完成后自动执行
  `Resources.UnloadUnusedAssets()` 清扫无引用资产（`Features.
  PreloadUnloadUnusedAfterSwap`）——additive 预载路径会按不同场景累积泄漏
  native 资产，长合集最终 OOM 硬崩（已定案），清扫即修复。完整问题清单与
  修复记录见 `ignored/M3遗留问题调查-反编译实证.md`。
- **保守预载（磁盘缓存预热，`Features.EnableDiskCacheWarmup`）**：与链式预载互补——
  单一后台线程（低优先级、固定 1MB 复用缓冲）把关卡的磁盘文件流式读一遍丢弃，装进
  OS 页缓存（内核管理、可回收，不占进程内存）；此后链式 hold 的读盘即变内存读，仅剩
  反序列化/集成的 CPU 负担。策略由 `Features.DiskWarmupMode` 选择：**follow-chain**
  （默认）——PREP 期（首关 hold 完成后）只头暖第 2-3 关，之后每当链式 hold N+1 完成，
  趁磁盘空闲窗口（上一 hold 已完成、下一 hold 未开始）预热第 N+2 关：页面从预热到
  使用约隔一关时长（几乎不会被逐出）、稳态占用小、本地 `lc` 练习局同样生效，代价是
  局内有温和的后台读；**prep-all**——PREP 期一次性暖全部后续关卡（局内零磁盘 IO）。
  开局（`round_start` 换入）或改图即协作式停止（当前文件读完即止）。内置关文件经
  引擎 build-settings 场景表映射（真机实证 43/43 场景可映射；映射失败自动退化为预热
  全部 `level*` 文件），共享资产按场景 build index 邻接裁剪（`sharedassets{N}.*`
  三件套，全合约 600MB 而非全量 4.1GB），workshop 关只读其加载路径真正读的两个文件
  （`metadata.json` + `data` 包），未安装项跳过、绝不触发下载。任何失败静默退化为
  现状。手动触发与冷/热缓存 A/B：`twi preload warm <levelId|all>`；`twi preload
  status` 的 `warm:` 行含模式与进度。
- **改图**：裁判重选图会重发 `pick_announced`，插件丢弃旧预载按新合集重来。
- 调试：`twi preload hold/swap/drop/status/rs/mach` 可在不连服务端的情况下手工验证
  驻留/换入/卸载（M1 原型，验证方案调研文档 §7 风险 1/2/3 用；关卡内 hold+swap
  即 M3 链式换入的最小复现，如 `twi preload hold Siege` 后换入验证投石机）。

> 完整设计见 `ignored/激进预载held-scene方案调研.md`；服务端侧（`pick_announced`
> 提前下发 + 预载门控）见 `ignored/需求-合集提前下发与预载门控.md`（后端实现后生效）。

## 端到端联调（服务端已存在）

1. 起后端：`cd TwilightCupBackend && uv run uvicorn twilightcupbackend.main:app --reload`（需本地 MongoDB）。
2. 用管理员账号建选手/裁判/导播账号，创建会话（指派人员、图池，每个 pick 的
   `collection.raw` 按上节格式）。
3. 选手端：编辑 `TwilightCore.cfg`（Username/Password），启动游戏 → 控制台输入 `twi connect <公网IP>`（默认端口 8443、默认走 `wss`/`https`，即 `https://<公网IP>:8443`：登录走 `/api/auth/login`，WS 走 `/ws/{token}`）→ 控制台依次回显登录/连接/已连接 → 收到 `auth_ok`。直连本地裸后端时用 `twi connect <本机IP> 8000` 并在 cfg 里把 `Server.UseTLS=false`（此时登录路径需为裸 `/auth/login`，不走 `/api`——仅本地无 nginx 时）。
4. 裁判端 `referee_mark_prep` → 选手 `!ready` → 双方就绪 → 观察 `countdown_tick` → `round_start` 下发 → 选手端自动加载合集第一关并按 LC 流程推进。
5. **模拟计时器**：真实通关会自动上报；或用 `twi sim level_done 12345` / `twi sim skip` / `twi sim complete` / `twi sim forfeit` 手动驱动 → 双方 terminal 后服务端进 `ROUND_JUDGING` → 裁判 `referee_verdict` → `round_result` + `cumulative_score` 正确。

## 已知限制 / 待办

- **真实计时器**：未实现（本期用 `SimulatedTimer` 占位）。
- **预载端到端**：held-scene 预载依赖服务端 `pick_announced`/门控（后端 R1/R2）；
  后端上线前仅可用 `twi preload …` 手工验证，正式比赛回合不受影响（自动走标准加载）。
- **重连重载合集**：回合中断线重连只补传双方状态快照，不会重新下发/加载合集配置
  （服务端 `reconnect_resync` 不含 pick/collection）；游戏崩溃后需手动重进。
  同一进程内的临时断连不会停止计时器，断线期间产生的上报会缓存并在重连后补发
  （`twi disconnect simulate` 可模拟该路径）。
- **聊天**：独立的 OnGUI 控制台（Ctrl+T），不依赖游戏内置 `NetChat`（后者在单人/菜单下被游戏锁死）。打开时仅抑制抓取/跳跃输入，**移动键（WASD）仍会生效**——停步后再打字；如需完全屏蔽移动，后续可在控制台打开时挂 `HumanControls` 补丁。
