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

## 端到端联调（服务端已存在）

1. 起后端：`cd TwilightCupBackend && uv run uvicorn twilightcupbackend.main:app --reload`（需本地 MongoDB）。
2. 用管理员账号建选手/裁判/导播账号，创建会话（指派人员、图池，每个 pick 的
   `collection.raw` 按上节格式）。
3. 选手端：编辑 `TwilightCore.cfg`（Username/Password），启动游戏 → 控制台输入 `twi connect <公网IP>`（默认端口 8443、默认走 `wss`/`https`，即 `https://<公网IP>:8443`：登录走 `/api/auth/login`，WS 走 `/ws/{token}`）→ 控制台依次回显登录/连接/已连接 → 收到 `auth_ok`。直连本地裸后端时用 `twi connect <本机IP> 8000` 并在 cfg 里把 `Server.UseTLS=false`（此时登录路径需为裸 `/auth/login`，不走 `/api`——仅本地无 nginx 时）。
4. 裁判端 `referee_mark_prep` → 选手 `!ready` → 双方就绪 → 观察 `countdown_tick` → `round_start` 下发 → 选手端自动加载合集第一关并按 LC 流程推进。
5. **模拟计时器**：真实通关会自动上报；或用 `twi sim level_done 12345` / `twi sim skip` / `twi sim complete` / `twi sim forfeit` 手动驱动 → 双方 terminal 后服务端进 `ROUND_JUDGING` → 裁判 `referee_verdict` → `round_result` + `cumulative_score` 正确。

## 已知限制 / 待办

- **真实计时器**：未实现（本期用 `SimulatedTimer` 占位）。
- **重连重载合集**：回合中断线重连只补传双方状态快照，不会重新下发/加载合集配置
  （服务端 `reconnect_resync` 不含 pick/collection）；游戏崩溃后需手动重进。
  同一进程内的临时断连不会停止计时器，断线期间产生的上报会缓存并在重连后补发
  （`twi disconnect simulate` 可模拟该路径）。
- **聊天**：独立的 OnGUI 控制台（Ctrl+T），不依赖游戏内置 `NetChat`（后者在单人/菜单下被游戏锁死）。打开时仅抑制抓取/跳跃输入，**移动键（WASD）仍会生效**——停步后再打字；如需完全屏蔽移动，后续可在控制台打开时挂 `HumanControls` 补丁。
