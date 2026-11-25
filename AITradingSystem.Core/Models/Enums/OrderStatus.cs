namespace AITradingSystem.Core.Models.Enums;

public enum OrderStatus
{
    New = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Canceled = 3,
    Rejected = 4,
    Expired = 5
}