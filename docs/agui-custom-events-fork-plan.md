# Fork `Microsoft.Agents.AI.AGUI` 并增加自定义事件支持

## Summary
目标是 fork / copy 现有 `Microsoft.Agents.AI.AGUI`，让客户端既能继续消费标准 AG-UI 事件，也能把你的自定义事件显式解析出来，并通过现有 `ChatResponseUpdate` 流暴露给上层，具体承载在 `AdditionalProperties` / `RawRepresentation` 中。

实现原则：
- 不改上层 `IChatClient` / `AGUIChatClient` 使用方式
- 未知事件不再抛异常
- 标准事件保留现有行为
- 自定义事件转换成“元数据型 `ChatResponseUpdate`”，由业务方从 `AdditionalProperties` 读取

## Key Changes
### 1. 复制并重命名 AGUI 客户端包
- 从 `dotnet/src/Microsoft.Agents.AI.AGUI` 复制出你自己的包，例如 `YourCompany.Agents.AI.AGUI`.
- 首批保留并改名这几类核心文件：
  - `AGUIChatClient.cs`
  - `AGUIHttpService.cs`
  - `Shared/BaseEvent*.cs`
  - `Shared/ChatResponseUpdateAGUIExtensions.cs`
  - `Shared/AGUIJsonSerializerContext.cs`
- 不要试图通过 DI 替换上游 `BaseEventJsonConverter`。
  - 上游代码使用 `AGUIJsonSerializerContext.Default.BaseEvent` 和静态 `ItemParser`
  - 扩展点不在 DI，而在 fork 后改反序列化链路

### 2. 新增可扩展事件模型
- 保留现有 `BaseEvent` 作为标准事件基类。
- 新增一个通用自定义事件类型，例如：
  - `CustomBaseEvent : BaseEvent`
  - 字段至少包含：
    - `string Type`
    - `JsonElement RawPayload`
    - `string? MessageId`
    - `string? ThreadId`
    - `string? RunId`
- 如果你的自定义事件有稳定 schema，再额外新增强类型事件，例如：
  - `PlanStepEvent`
  - `ApprovalRequestedEvent`
  - `UiWidgetEvent`
- 定义一个事件注册表接口，例如：
  - `ICustomAGUIEventResolver`
  - 输入：`JsonElement` + `type`
  - 输出：具体 `BaseEvent` 子类，或者 `CustomBaseEvent`
- 默认行为：
  - 标准 `type` 仍映射到原有事件类
  - 非标准 `type` 先尝试命中你注册的自定义类型
  - 都不命中时降级为 `CustomBaseEvent`
- 不采用“完全忽略未知事件”的默认策略，因为你的目标是显式暴露。

### 3. 替换事件反序列化器
- 复制 `BaseEventJsonConverter` 为自定义版本，例如 `ExtensibleBaseEventJsonConverter`.
- 去掉 `sealed` 限制并改成组合式分发，不靠继承。
- 在 `Read` 中改成三段式逻辑：
  1. 标准事件映射
  2. 自定义 resolver 映射
  3. fallback 到 `CustomBaseEvent`
- 在 `Write` 中同样支持：
  - 标准事件序列化
  - 你的强类型自定义事件序列化
  - `CustomBaseEvent.RawPayload` 原样写出
- 更新 `BaseEvent` 上的 `[JsonConverter(...)]` 指向你的新 converter。
- 更新 `AGUIJsonSerializerContext`，把新增的自定义事件类型都加入 `[JsonSerializable(...)]`。
- 如果你允许运行时注册任意自定义类型，避免完全依赖 source-gen；在 fork 包里给 `JsonSerializerOptions` 挂上自定义 resolver/contract 逻辑，并让 `AGUIHttpService` 真正使用这套 options。

### 4. 改 `AGUIHttpService` 让它使用可注入序列化配置
- 修改 `AGUIHttpService` 构造函数，接收：
  - `JsonSerializerOptions`
  - 可选 `ICustomAGUIEventResolver`
- 把 `ItemParser` 从静态硬编码：
  - `JsonSerializer.Deserialize(data, AGUIJsonSerializerContext.Default.BaseEvent)`
  改为基于实例 options 反序列化。
- 这样 SSE 解析就会走你 fork 后的 converter，而不是上游默认白名单。
- 保持请求体 `RunAgentInput` 的写法不变，除非你也要扩展入站消息格式。

### 5. 把自定义事件暴露为 `ChatResponseUpdate`
- 修改 `ChatResponseUpdateAGUIExtensions.AsChatResponseUpdatesAsync(...)` 的 `switch (evt)`。
- 新增 `case CustomBaseEvent custom:` 分支。
- 输出一个“元数据型更新”：
  - `Role = ChatRole.Assistant`
  - `Contents = []`
  - `ConversationId` / `ResponseId` 从当前 run 上下文继承
  - `RawRepresentation = custom`
  - `AdditionalProperties` 至少包含：
    - `["agui_event_type"] = custom.Type`
    - `["agui_event_kind"] = "custom"`
    - `["agui_event_payload"] = custom.RawPayload`
- 如果自定义事件本身带 `messageId` / `threadId` / `runId`，同步填到：
  - `MessageId`
  - `ConversationId`
  - `ResponseId`
  但只有在值非空且不破坏当前 run 语义时才覆盖。
- 若是强类型自定义事件，再额外写一层 mapper：
  - `PlanStepEvent -> AdditionalProperties["plan_step"]`
  - `ApprovalRequestedEvent -> AdditionalProperties["approval"]`
- 不要把自定义事件伪装成 `TextContent`，否则会污染消息聚合和 tool flow。

### 6. 上层消费约定
- 上层业务以如下规则识别自定义事件：
  - `update.AdditionalProperties["agui_event_kind"] == "custom"`
- 读取方式约定统一：
  - `agui_event_type`
  - `agui_event_payload`
  - `RawRepresentation`
- 保持标准事件消费逻辑不变：
  - 文本仍看 `TextContent`
  - 工具仍看 `FunctionCallContent` / `FunctionResultContent`
  - 状态仍看 `DataContent`
- 如果你后面还要给 UI 单独做分发层，可以再在应用层包一层 adapter，把这些 metadata update 转成业务事件对象；这一步不放进 fork 包里。

## Public API / Types
- 新增 `CustomBaseEvent : BaseEvent`
- 新增 `ICustomAGUIEventResolver`
- 新增 fork 版客户端，例如 `ExtensibleAGUIChatClient`
- `ExtensibleAGUIChatClient` 构造函数增加：
  - `JsonSerializerOptions?`
  - `ICustomAGUIEventResolver?`
- 现有 `GetStreamingResponseAsync` 和 `GetResponseAsync` 方法签名保持不变。
- 语义变化：
  - 以前遇到未知 `type` 抛异常
  - 现在返回一个空 contents 的 `ChatResponseUpdate`，并在 `AdditionalProperties` 中携带自定义事件信息

## Test Plan
- 标准事件回归：
  - `RUN_*`
  - `TEXT_MESSAGE_*`
  - `TOOL_CALL_*`
  - `STATE_SNAPSHOT` / `STATE_DELTA`
  - `REASONING_*`
  行为与 fork 前一致
- 未知事件兼容：
  - 服务端发送一个未注册自定义事件
  - 断言不抛异常
  - 断言生成 1 条 metadata 型 `ChatResponseUpdate`
  - 断言 `AdditionalProperties["agui_event_type"]` 与原始 `type` 一致
- 已注册强类型事件：
  - 发送你定义的事件 JSON
  - 断言被反序列化为对应强类型
  - 断言 `RawRepresentation` / `AdditionalProperties` 中都能拿到
- 混合流：
  - 文本、自定义事件、工具调用、自定义事件、状态更新交错出现
  - 断言顺序保持一致
  - 断言标准事件聚合不被自定义事件打断
- 错误场景：
  - 自定义事件 payload 不符合强类型 schema
  - 若配置为宽松模式，fallback 到 `CustomBaseEvent`
  - 若配置为严格模式，抛出带 `type` 和原始 payload 摘要的异常
- 多轮会话：
  - 自定义事件出现后，下一轮 threadId / runId 处理仍正常
  - 不影响本地工具自动调用链路

## Assumptions
- 你控制服务端事件格式，可以保证每个自定义事件都有稳定的 `type`。
- 你希望上层继续基于 `ChatResponseUpdate` 工作，而不是引入新的公开流类型。
- 自定义事件主要是给应用/UI 层消费，不需要被 `FunctionInvokingChatClient` 当作工具或文本参与推理。
- 默认采用宽松模式：
  - 未知自定义事件降级为 `CustomBaseEvent`
  - 不因单个未知事件中断整个 SSE 流
