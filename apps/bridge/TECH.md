# TECH：本插件依赖的 AstrBot 内部符号

本插件不是「调 AstrBot 的公开 API」，而是作为平台适配器钻进它的消息管线：构造事件、复用 QQ 会话的 unified_msg_origin、截住回复文本。这些位置都在 `astrbot.core` 里，属于内部实现，**AstrBot 小版本升级就可能变**。本文件把依赖列表、失效表现与降级方式写下来，便于升级后 5 分钟内定位。

依据版本：AstrBot **4.28.2**（`D:\Astrbot\backend\app\astrbot\__init__.py` 的 `__version__`）。行号取自该版本。

## 1. 依赖清单

| 符号 | 位置 | 我们怎么用 | 失效表现 |
| --- | --- | --- | --- |
| `AstrMessageEvent.unified_msg_origin` setter | `astrbot/core/platform/astr_message_event.py:105-114`（setter 内 `MessageSession.from_str`） | 构造事件后把 umo 改指到 `Monika:FriendMessage:<QQ号>`，从而共用 conversation / persona / 记忆插件 / provider | setter 消失 → 角色变成「失忆」；插件降级到独立 umo 并在日志与前端告警 |
| `AstrMessageEvent.__init__(message_str, message_obj, platform_meta, session_id)` | 同文件 | 自定义事件 `GameMessageEvent` 的基类构造 | 构造报 TypeError，回合直接失败 |
| `AstrBotMessage` / `MessageMember` / `MessageType.FRIEND_MESSAGE` | `astrbot/core/message` | 伪造一条「好友私聊」消息对象，让管线按私聊处理（免唤醒前缀） | 管线判成群聊 → 要求 @ 或唤醒词 |
| `event.send(chain)` / `event.send_streaming(gen)` | `astr_message_event.py`（基类 `send_streaming` 是空实现） | 覆写成把文本收进 `sent_chunks` 并回调 WS，**不发往 QQ** | 覆写点改名 → 回复丢失或漏到 QQ |
| `filter.on_llm_response` / `filter.on_decorating_result` | `astrbot/api/event`（注册实现 `core/star/register/star_handler.py:456` / `:730`） | 在管线里存整轮文本、抑制第三方分段插件、跳过平台自带 TTS | 钩子不触发 → 只能拿到 `event.send` 的片段（多段回复会只剩最后一段） |
| `OnDecoratingResultEvent` 钩子顺序 | `core/pipeline/result_decorate/stage.py:159-173`；排序见 `core/star/star_handler.py:26`（priority 降序） | 我们 priority 0，早于 `astrbot_plugin_splitter` 的 `-100000000000000000` | 顺序反转 → 又只能拿到最后一段 |
| `ResultContentType.LLM_RESULT` | `astrbot/core/message/message_event_result.py:208` | 把游戏回合的内容类型降为 `GENERAL_RESULT`，跳过 `result_decorate` 的 TTS 分支 | 枚举改名 → 跳过失效，每回合多 20 秒且大概率 400 |
| `MessageEventResult.get_plain_text()` | 同文件 `:149` | 从结果链里取纯文本 | 取不到 → 退回 `sent_chunks` 拼接 |
| `MessageEventResult` 可 `setattr` | 同文件 `:224`（dataclass，无 `__slots__`） | 打上 `__splitter_processed`，让分段插件整条跳过 | 加不了属性 → 分段插件重新切段并直投 QQ |
| `active_event_registry._events` | `astrbot/core/utils/active_event_registry.py` | 判断事件是否已被管线消费完（回合结束的信号） | 私有字段改名 → 回合超时；改走 `sent_chunks` 已有超时兜底 |
| `context.get_event_queue()` | `astrbot/core/star/context.py` | 把事件投进 AstrBot 的 `event_queue` | 拿不到 → 插件标记 `degraded=true`，`/health` 明示 |

## 2. 刻意不用的东西

- **`context.register_web_api`**：只支持 HTTP、且只能经面板 `/api/plug/<plugin_path>` 转发并需要面板 JWT（`core/star/context.py:705`、`dashboard/api/plugins.py:166/184`），拿不到裸 WebSocket。所以插件在自身进程里起 `uvicorn`，只绑 `127.0.0.1:8790`。
- **`webchat` 平台适配器**：`core/platform/sources/webchat/webchat_adapter.py:213` 把 session_id 强制写成 `webchat!<user>!<cid>`，无法伪装成 QQ 会话，因此「共用记忆」这条需求做不到。
- **`provider_tts_settings`**：平台自带 TTS 按 umo 查 `SessionServiceManager`（`core/star/session_llm_manager.py:144`），与 QQ 共用、按事件关不掉，所以改用内容类型降级绕开。

## 3. 升级检查清单

升级 AstrBot 后依次确认：

1. 插件日志有 `mas3d_game <version> loaded (umo=...)` 且没有「拿不到 AstrBot 的事件队列」。
2. `GET /mas3d/api/health` 返回 `degraded: false`。
3. 发一回合，WS 收到的文本与 `data_v4.db` 里 conversations 最新 assistant 文本逐字一致（多段也要一致）。
4. `backend.log` 里该回合没有 `[Splitter]` 分段记录、没有 `TTS request`。
5. `platform_message_history` 没有新行（说明没投递到 QQ）。

任一项不符，按第 1 节的「失效表现」定位；实在跟不上内部变更时，**停用本插件即恢复 QQ 原状**——插件不改 AstrBot 配置、不写它的数据库、除了两个钩子之外不注册全局状态，所有副作用都发生在一条我们自己构造的事件上。

## 4. 已知副作用

- 游戏回合的文本会以 `（3D教室）` 前缀写进 QQ 会话历史（这是需求：两边共用记忆）。回合数在对话面板里可见。
- 因为共用 umo，`astrbot_plugin_splitter`、`livingmemory`、`st_preset` 等按 umo 生效的插件对游戏回合同样生效；我们只对「分段发送」做了抑制，其余（记忆写入、人设）按设计保留。
