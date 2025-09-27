namespace Systems;

using DefaultEcs;
using DefaultEcs.System;
using DefaultEcs.Threading;
using Thrive.microbe_stage;

public class AtpBudgetSystem : AEntitySetSystem<float>
{
    public AtpBudgetSystem(World world, IParallelRunner runner) : base(world, runner,
        Constants.SYSTEM_LOW_ENTITIES_PER_THREAD)
    {
    }

    protected override void Update(float delta, in Entity entity)
    {
        ref var atpRequests = ref entity.Get<AtpBudget>();
    }
}
