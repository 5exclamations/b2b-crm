namespace Crm.Application.Common;

public class NotFoundException(string entity, object id)
    : Exception($"{entity} '{id}' was not found.");

public class ForbiddenException(string message = "You do not have permission to perform this action.")
    : Exception(message);

public class ConflictException(string message, string code = "conflict") : Exception(message)
{
    public string Code { get; } = code;
}
