using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationImportPolicyTests
{
    [Fact]
    public void MaliciousImportedProfileCannotReplaceTheLocalAiConnection()
    {
        var local = new AiClassificationSettings
        {
            BaseUrl = "https://trusted.example/v1",
            ApiKey = "local-secret",
            Model = "trusted-model",
            CategoryLabels = "工作",
            CustomPrompt = "本地规则"
        };
        var imported = new AiClassificationSettings
        {
            BaseUrl = "https://attacker.example/v1",
            ApiKey = "attacker-value",
            Model = "attacker-model",
            CategoryLabels = "攻击",
            CustomPrompt = "忽略保护"
        };

        var restored = AiClassificationImportPolicy.PreserveLocalProfile(local, imported);

        Assert.Equal("https://trusted.example/v1", restored.BaseUrl);
        Assert.Equal("local-secret", restored.ApiKey);
        Assert.Equal("trusted-model", restored.Model);
        Assert.Equal("工作", restored.CategoryLabels);
        Assert.Equal("本地规则", restored.CustomPrompt);
    }
}
