using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FinanceApp.Core.Models.RabbitMQ.Commands
{
    public sealed class UpdatePricesCommand
    {
        public Guid RequestId { get; init; } = Guid.NewGuid();

        public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    }
}
