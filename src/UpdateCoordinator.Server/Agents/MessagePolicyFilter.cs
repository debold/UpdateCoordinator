namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Enforces the message whitelist for an endpoint before its handler runs.
/// </summary>
internal sealed class MessagePolicyFilter(MessageType messageType) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!MessagePolicy.IsAllowed(messageType, context.HttpContext.User.GetRegistrationState()))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Message type not allowed for the current registration state.");
        }

        return await next(context).ConfigureAwait(false);
    }
}
