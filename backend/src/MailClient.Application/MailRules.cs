namespace MailClient.Application.Mail;

public sealed record RuleCondition(string Type, string? Value);
public sealed record RuleAction(string Type, Guid? FolderId = null, Guid? LabelId = null);
public sealed record RuleRequest(string Name, bool Enabled, int Priority, string Logic,
    IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions, string? LegacyId = null);
public sealed record RuleResponse(Guid Id, string Name, bool Enabled, int Priority, string Logic,
    IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions,
    DateTime CreatedAt, DateTime UpdatedAt);
