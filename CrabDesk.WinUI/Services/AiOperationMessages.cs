using CrabDesk.Core;

namespace CrabDesk.WinUI.Services;

internal static class AiOperationMessages
{
    internal static string ToUserMessage(Exception exception) => exception switch
    {
        OperationCanceledException => "AI 整理已取消。",
        AiClassificationRequestException { LengthTruncated: true } =>
            "AI 服务响应被 max_tokens 截断（模型思考或输出过长），已自动加大额度重试仍失败。请减少单次整理的项目数量后重试。",
        AiClassificationRequestException { StatusCode: { } statusCode } =>
            $"AI 服务请求失败（HTTP {statusCode}），请检查接口配置后重试。",
        AiClassificationRequestException => AiClassificationRequestException.SafeMessage,
        InvalidDataException error => error.Message,
        InvalidOperationException { Message: "AI 整理正在运行，请等待当前操作完成。" } =>
            "AI 整理正在运行，请等待当前操作完成。",
        InvalidOperationException { Message: "桌面状态已变化，请重新预览 AI 整理结果。" } =>
            "桌面状态已变化，请重新预览 AI 整理结果。",
        _ => "AI 操作失败，请检查配置后重试。"
    };
}
