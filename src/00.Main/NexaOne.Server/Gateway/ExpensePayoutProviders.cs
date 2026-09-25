using NexaOne.ServiceContracts.Erp;

namespace NexaOne.Server.Gateway;

/// <summary>Host registry. Provider adapters can be added without giving the ERP module credential access.</summary>
public sealed class ExpensePayoutProviderRegistry : IExpensePayoutProviderRegistry
{
    public IExpensePayoutProvider GetRequired(string providerKey)
        => throw new ExpensePayoutProviderException(ExpensePayoutProviderError.UnsupportedProvider);
}
