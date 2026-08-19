# TwilightCore

黄昏杯（Twilight Cup）比赛的**选手端** BepInEx 插件，用于《人类一败涂地》(Human: Fall Flat)。
内嵌 [LevelCollections](https://github.com/...) 合集引擎，并连接已存在的
`TwilightCupBackend` 服务端（FastAPI + WebSocket），实现：聊天、`!ready`、阶段/倒计时、
服务端下发合集自动开跑、准备阶段锁定（防提前起跑），以及一套**模拟计时器**让比赛流程
不依赖真实计时器也能跑通到计分/判定。

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
| `Features.PreloadUnloadUnusedAfterSwap` | `true` | **换入后清扫**：每次换入完成、旧场景卸载后执行 `Resources.UnloadUnusedAssets()` 并等待（约 50-145ms）。additive 预载路径会按不同场景累积泄漏 native 资产，长合集（约 12 个不同场景起）最终在场景加载时 OOM 硬崩；清扫即修复（代价是每次换入多一次短卡顿），关闭可做 A/B 对比 |
| `Chat.PopupEnabled` | `true` | 收到消息时弹出仅日志的聊天框（无输入框），随后淡出 |
| `Chat.PopupSecs` | `5` | 上述弹出框持续秒数（之后淡出） |
| `Chat.ToggleHotkey` | `Ctrl+T` | 打开/关闭聊天控制台的快捷键，格式 `修饰键+主键`，如 `Ctrl+T`、`Ctrl+Shift+Y`、`Alt+F8`、`F8`。`Ctrl` 在 macOS 上同时匹配 `Cmd`。可用 `twi reload` 热生效（无需重启） |
| `HUD.Enabled` | `true` | 合集运行期间在屏幕右上角显示两行合集信息（样式仿计时器：粗体纯文本、单色无渐变） |
| `HUD.TextColor` | `FFD94C` | HUD 文本颜色，hex 编码（`RRGGBB` 或 `RRGGBBAA`，可带 `#`） |
| `HUD.FontSize` | `18` | HUD 字号（与 TwilightTimer 计时器默认一致） |
| `Debug.VerboseNetLog` | `false` | 打印每条收发帧 |

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
| `twi preload hold <levelId>` | **M1/M3 验证**：把指定关卡以 additive+休眠方式驻留（不经比赛流程）；主菜单或游玩中的关卡内均可（后者即链式预载形态）。id 同合集配置：内置关用显示名（`Aztec`、`Steam`…，区分大小写）或已订阅的 workshop id |
| `twi preload swap` | **M1/M3 验证**：把驻留场景换入为当前关（GO 换入序列；勿在 ready 锁定中使用） |
| `twi preload drop` | 丢弃驻留场景并卸载 |
| `twi preload status` | 预载器状态（选图/驻留场景/失败原因） |
| `twi preload rs` | 转储当前全局光照状态（探针/环境光/雾/光照贴图表逐条纹理标识/逐场景 renderer lightmapIndex 直方图/LOD 层状态/进程内存），排查换入光照与内存问题用 |
| `twi preload mach` | 转储当前关全部机器的关节/物理状态（AngularJoint/Lever/Catapult/铰链角度与驱动目标等），机器异常时当场运行 |

聊天：
- **Ctrl+T**（可由 `Chat.ToggleHotkey` 配置，如 `Ctrl+Shift+Y`、`F8` 等）打开/关闭完整聊天控制台（菜单和局内都可用），输入文本回车发送；`!ready`、`!roll` 等就是普通聊天文本。控制台打开时会接管键盘，游戏不会响应抓取/跳跃（移动键仍可能生效，请停步后再打字）。改完快捷键用 `twi reload` 热生效。
- 收到消息时（控制台未打开），会自动弹出**仅显示日志（无输入框）**的聊天框，持续 `Chat.PopupSecs`（默认 5 秒）后淡出；有新消息会顺延。可由 `Chat.PopupEnabled` 关闭。

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
  修复（保持期内探针系数冻结为玩家处采样值，换入时写回）；其余未烘焙关维持
  轻微的下一关染色，换关即恢复（已接受的取舍）。换入完成后自动执行
  `Resources.UnloadUnusedAssets()` 清扫无引用资产（`Features.
  PreloadUnloadUnusedAfterSwap`）——additive 预载路径会按不同场景累积泄漏
  native 资产，长合集最终 OOM 硬崩（已定案），清扫即修复。完整问题清单与
  修复记录见 `ignored/M3遗留问题调查-反编译实证.md`。
- **改图**：裁判重选图会重发 `pick_announced`，插件丢弃旧预载按新合集重来。
- 调试：`twi preload hold/swap/drop/status/rs` 可在不连服务端的情况下手工验证
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
