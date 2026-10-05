namespace RtsSkillStudio.Agent.Llm;

public interface ILlmProvider
{
    LlmProviderDescriptor Descriptor { get; }

    Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken
    );
}
