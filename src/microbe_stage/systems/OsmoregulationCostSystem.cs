namespace Systems;

using System;
using Components;
using DefaultEcs;
using DefaultEcs.System;
using DefaultEcs.Threading;
using Godot;
using Thrive.microbe_stage;

public class OsmoregulationCostSystem : AEntitySetSystem<float>
{
    private GameWorld? gameWorld;

    public OsmoregulationCostSystem(World world, IParallelRunner parallelRunner) :
        base(world, parallelRunner)
    {
    }

    public void SetWorld(GameWorld world)
    {
        gameWorld = world;
    }

    protected override void PreUpdate(float state)
    {
        base.PreUpdate(state);

        if (gameWorld == null)
            throw new InvalidOperationException("GameWorld not set");
    }

    protected override void Update(float delta, in Entity entity)
    {
        ReserveOsmoregulationEnergyCost(entity, delta);
    }

    private void ReserveOsmoregulationEnergyCost(in Entity entity, float delta)
    {
        ref var cellProperties = ref entity.Get<CellProperties>();
        ref var atpBudget = ref entity.Get<AtpBudget>();
        ref var organelles = ref entity.Get<OrganelleContainer>();

        var environmentalMultiplier = 1.0f;

        var osmoregulationCost = organelles.HexCount * cellProperties.MembraneType.OsmoregulationFactor *
            Constants.ATP_COST_FOR_OSMOREGULATION * delta;

        var colonySize = 0;
        if (entity.Has<MicrobeColony>())
        {
            colonySize = entity.Get<MicrobeColony>().ColonyMembers.Length;
        }
        else if (entity.Has<MicrobeColonyMember>() &&
                 entity.Get<MicrobeColonyMember>().GetColonyFromMember(out var colonyEntity))
        {
            colonySize = colonyEntity.Get<MicrobeColony>().ColonyMembers.Length;
        }

        // 5% osmoregulation bonus per colony member
        if (colonySize != 0)
        {
            osmoregulationCost *= 20.0f / (20.0f + colonySize);
        }

        // TODO: remove this check on next save breakage point
        if (entity.Has<MicrobeEnvironmentalEffects>())
        {
            ref var environmentalEffects = ref entity.Get<MicrobeEnvironmentalEffects>();
            environmentalMultiplier = environmentalEffects.OsmoregulationMultiplier;

            // TODO: remove this safety check once it is no longer possible for this problem to happen
            // https://github.com/Revolutionary-Games/Thrive/issues/5928
            if (float.IsNaN(environmentalMultiplier) || environmentalMultiplier < 0)
            {
                GD.PrintErr("Microbe has invalid osmoregulation multiplier: ", environmentalMultiplier);

                // Reset the data to not spam the error
                environmentalEffects.OsmoregulationMultiplier = 1.0f;

                environmentalMultiplier = 1.0f;
            }
        }

        osmoregulationCost *= environmentalMultiplier;

        // Only player species benefits from lowered osmoregulation
        if (entity.Get<SpeciesMember>().Species.PlayerSpecies)
            osmoregulationCost *= gameWorld!.WorldSettings.OsmoregulationMultiplier;

        atpBudget.SubmitAtpRequest(osmoregulationCost, true);
    }
}
