using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Judges whether a ComfyUI result satisfies the user's original prompt. Internal
/// collaborator of <see cref="ImageEditOrchestrator"/> &mdash; not registered in DI on its own.</summary>
internal sealed class ImageEditJudge
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonNode JudgeSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "satisfied": { "type": "boolean" },
            "feedback": { "type": "string" }
          },
          "required": ["satisfied", "feedback"],
          "additionalProperties": false
        }
        """)!;

    private readonly ILlmClient _llm;

    public ImageEditJudge(ILlmClient llm)
    {
        _llm = llm;
    }

    public async Task<(bool Satisfied, string Feedback)> JudgeAsync(
        EditRequest request, string refinedPrompt, WorkflowDefinition workflow, byte[] resultImageBytes, CancellationToken ct)
    {
        const string systemPrompt = """
            You are the judging stage of an AI image-editing assistant built on ComfyUI.
            Compare the generated result against the user's original instructions and decide whether it
            satisfies them. Be specific and actionable in your feedback if it does not.

            Respond with ONLY a JSON object of this exact shape, no other text:
            {"satisfied": true|false, "feedback": "<specific, actionable feedback if not satisfied, else a brief confirmation>"}
            """;

        var userText = new StringBuilder();
        userText.AppendLine($"Original user instructions: {request.Prompt}");
        userText.AppendLine($"Refined prompt used: {refinedPrompt}");
        userText.AppendLine($"Workflow used: {workflow.DisplayName} ({workflow.Description})");
        userText.AppendLine("The first image below is the result. The remaining images are the original source images" +
                             (request.Mask is not null ? ", followed by the mask." : "."));

        var images = new List<byte[]> { resultImageBytes };
        images.AddRange(request.Images.Select(i => i.GetLlmPreviewBytes()));
        if (request.Mask is not null)
        {
            images.Add(request.Mask.Bytes);
        }

        var llmRequest = new LlmRequest(
            systemPrompt,
            [new LlmMessage(LlmRole.User, userText.ToString(), images)],
            new JsonResponseSchema("image_edit_judgement", JudgeSchema));

        var raw = await _llm.CompleteAsync(llmRequest, ct);
        var dto = TolerantJson.Parse<JudgeDto>(raw, JsonOptions);
        return (dto.Satisfied, dto.Feedback);
    }

    private sealed record JudgeDto(bool Satisfied, string Feedback);
}
