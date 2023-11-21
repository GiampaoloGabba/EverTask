using EverTask.Scheduler.Builder;

namespace EverTask.Tests
{
    public class RecurringTaskBuilderTests
    {
        private readonly RecurringTaskBuilder _builder = new();

        [Fact]
        public void Should_set_RunNow_to_true_and_SpecificRunTime_to_current_time()
        {
            _builder.RunNow();

            Assert.True(_builder.RecurringTask.RunNow);

            var currentTime     = DateTimeOffset.UtcNow;
            var specificRunTime = _builder.RecurringTask.SpecificRunTime!.Value;

            var roundedCurrentTime     = currentTime.AddTicks(-currentTime.Ticks);
            var roundedSpecificRunTime = specificRunTime.AddTicks(-specificRunTime.Ticks);

            roundedSpecificRunTime.ShouldBe(roundedCurrentTime);
        }

        [Fact]
        public void RunDelayed_Should_set_InitialDelay()
        {
            var delay = TimeSpan.FromMinutes(1);

            _builder.RunDelayed(delay);

            _builder.RecurringTask.InitialDelay!.Value.ShouldBe(delay);
        }

        [Fact]
        public void RunAt_Should_set_SpecificRunTime()
        {
            var dateTimeOffset = DateTimeOffset.UtcNow.AddHours(1);

             _builder.RunAt(dateTimeOffset);

            _builder.RecurringTask.SpecificRunTime!.Value.ShouldBe(dateTimeOffset);
        }

        [Fact]
        public void Should_set_cron_and_MaxRuns()
        {
            var cronExpression = "* * * * *";

            _builder.Schedule().UseCron(cronExpression).MaxRuns(3);

            _builder.RecurringTask.CronExpression.ShouldBe(cronExpression);
            _builder.RecurringTask.MaxRuns.ShouldBe(3);
        }

        [Fact]
        public void Should_set_complex_expression()
        {
            var dateTimeOffset = DateTimeOffset.UtcNow.AddHours(1);
            var cronExpression = "* * * * *";

            _builder.RunAt(dateTimeOffset).Then().UseCron(cronExpression).MaxRuns(3);

            _builder.RecurringTask.SpecificRunTime!.Value.ShouldBe(dateTimeOffset);
            _builder.RecurringTask.CronExpression.ShouldBe(cronExpression);
            _builder.RecurringTask.MaxRuns.ShouldBe(3);
        }
    }
}
