namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// Marks the single hosted runtime that applies durable WorkScope projection evidence.
/// The contract lives in the Default ALC so the host can validate optional plugin composition
/// without depending on the POM implementation assembly.
/// </summary>
public interface IWorkScopeProjectionRuntime
{
}
