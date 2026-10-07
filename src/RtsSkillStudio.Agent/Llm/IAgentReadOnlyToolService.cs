namespace RtsSkillStudio.Agent.Llm;

public interface IAgentReadOnlyToolService
{
    IReadOnlyList<AgentToolDefinition> Definitions { get; }

    Task<IReadOnlyList<AgentToolExecution>> ExecuteAsync(
        IReadOnlyList<AgentToolCall> calls,
        CancellationToken cancellationToken
    );
}
