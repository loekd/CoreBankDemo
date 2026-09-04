using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.CloudEventTypes;

/// <summary>
/// Story 3.3: pins <see cref="Constants"/> to the exact legacy CloudEvent type
/// strings (AD-1/AD-12). These are wire contract values shared with every
/// consumer that subscribes to the transaction-events topic — not open to
/// incidental change.
/// </summary>
public class ConstantsTests
{
    [Fact]
    public void TransactionCompleted_matches_the_legacy_literal()
    {
        Constants.TransactionCompleted.Should().Be("com.corebank.transaction.completed");
    }

    [Fact]
    public void TransactionFailed_matches_the_legacy_literal()
    {
        Constants.TransactionFailed.Should().Be("com.corebank.transaction.failed");
    }

    [Fact]
    public void BalanceUpdated_matches_the_legacy_literal()
    {
        Constants.BalanceUpdated.Should().Be("com.corebank.account.balance.updated");
    }
}
