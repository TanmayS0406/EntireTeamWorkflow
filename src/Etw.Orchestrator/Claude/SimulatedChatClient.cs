using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Etw.Orchestrator.Claude;

/// <summary>
/// A stand-in for <see cref="ClaudeCodeChatClient"/> that makes no Claude calls and writes no files. It streams a short
/// scripted "work session" so the workflow, the parallel lanes and the notifications can be demonstrated and tested for free.
/// </summary>
public sealed class SimulatedChatClient(string role, IAgentReporter reporter, int stepDelayMs = 700) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (role == "analyst")
        {
            await Task.Delay(stepDelayMs, cancellationToken);
            const string questions = "[{\"id\":\"q1\",\"text\":\"Should users have to sign in, or is the app anonymous?\"},{\"id\":\"q2\",\"text\":\"Is there an existing data source to integrate with?\"}]";
            reporter.Text(questions);
            yield return new ChatResponseUpdate(ChatRole.Assistant, questions);
            yield break;
        }

        foreach (var (kind, text) in Script(role))
        {
            await Task.Delay(stepDelayMs, cancellationToken);
            if (kind == 'A')
            {
                reporter.Activity(text);
            }
            else
            {
                reporter.Text(text + "\n");
                yield return new ChatResponseUpdate(ChatRole.Assistant, text + "\n");
            }
        }
    }

    private static IEnumerable<(char Kind, string Text)> Script(string role) => role switch
    {
        "architect" =>
        [
            ('T', "Reading the user stories and choosing the architecture."),
            ('A', "Write: docs/architecture.md"),
            ('A', "Write: contracts/openapi.yaml"),
            ('T', "Contract defines the endpoints both teams build against."),
            ('A', "Write: docs/tasks.md"),
            ('T', "Plan ready."),
        ],
        "backend" =>
        [
            ('T', "Implementing the API from the contract."),
            ('A', "Write: backend/Program.cs"),
            ('A', "Write: backend/Endpoints/Items.cs"),
            ('A', "Bash: dotnet build"),
            ('A', "Write: backend/Tests/ItemsTests.cs"),
            ('A', "Bash: dotnet test"),
            ('T', "All backend tests pass."),
        ],
        "frontend" =>
        [
            ('T', "Building the screens against the contract with a mock API."),
            ('A', "Write: frontend/src/api/client.ts"),
            ('A', "Write: frontend/src/pages/Items.tsx"),
            ('A', "Bash: npm run build"),
            ('A', "Write: frontend/src/pages/Items.test.tsx"),
            ('T', "UI builds and its tests pass."),
        ],
        "qa" =>
        [
            ('T', "Deriving test cases from the acceptance criteria."),
            ('A', "Write: tests/api/items.spec"),
            ('A', "Write: docs/test-plan.md"),
            ('T', "Test suite ready to run against the backend."),
        ],
        "integrator" =>
        [
            ('T', "Building both halves and running every suite."),
            ('A', "Bash: dotnet test"),
            ('A', "Bash: npm test"),
            ('A', "Write: README.md"),
            ('A', "Write: docs/integration-report.md"),
            ('T', "Everything builds and passes."),
        ],
        "repo" =>
        [
            ('T', "Reading the plan and the existing code of this repository."),
            ('A', "Read: .etw/docs/plan.md"),
            ('A', "Edit: src/Services/ExistingService.cs"),
            ('A', "Bash: dotnet build"),
            ('A', "Edit: tests/ExistingServiceTests.cs"),
            ('A', "Bash: dotnet test"),
            ('T', "Change implemented; the existing tests and the new ones pass."),
        ],
        _ =>
        [
            ('T', "Working on it."),
            ('A', "Write: docs/notes.md"),
            ('T', "Done."),
        ],
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
