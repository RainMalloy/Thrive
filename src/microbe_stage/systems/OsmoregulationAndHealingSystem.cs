﻿namespace Systems;

using System;
using Components;
using DefaultEcs;
using DefaultEcs.System;
using DefaultEcs.Threading;
using Thrive.microbe_stage;

/// <summary>
///     Handles damage due to insufficient ATP, damage due to hydrogen sulfide, and passive health regeneration.
/// </summary>
/// <remarks>
///     <para>
///         This is marked as just reading <see cref="MicrobeStatus" /> as this has a reserved variable in it just for
///         this systems use so writing to it doesn't conflict with other systems.
///     </para>
/// </remarks>
[With(typeof(OrganelleContainer))]
[With(typeof(CellProperties))]
[With(typeof(MicrobeStatus))]
[With(typeof(CompoundStorage))]
[With(typeof(Engulfable))]
[With(typeof(Health))]
[With(typeof(AtpBudget))]
[ReadsComponent(typeof(OrganelleContainer))]
[ReadsComponent(typeof(CellProperties))]
[ReadsComponent(typeof(MicrobeStatus))]
[ReadsComponent(typeof(Engulfable))]
[RunsAfter(typeof(PilusDamageSystem))]
[RunsAfter(typeof(DamageOnTouchSystem))]
[RunsAfter(typeof(ToxinCollisionSystem))]
[RuntimeCost(4)]
public sealed class OsmoregulationAndHealingSystem : AEntitySetSystem<float>
{
    private bool hydrogenSulfideDamageTrigger;
    private float elapsedSinceTrigger;

    public OsmoregulationAndHealingSystem(World world, IParallelRunner parallelRunner) :
        base(world, parallelRunner)
    {
    }

    protected override void PreUpdate(float state)
    {
        base.PreUpdate(state);

        elapsedSinceTrigger += state;

        if (elapsedSinceTrigger >= Constants.HYDROGEN_SULFIDE_DAMAGE_INTERVAL)
        {
            hydrogenSulfideDamageTrigger = true;
            elapsedSinceTrigger = 0.0f;
        }
        else
        {
            hydrogenSulfideDamageTrigger = false;
        }
    }

    protected override void Update(float delta, in Entity entity)
    {
        ref var status = ref entity.Get<MicrobeStatus>();
        ref var health = ref entity.Get<Health>();
        ref var cellProperties = ref entity.Get<CellProperties>();

        // Dead cells may not regenerate health
        if (health.Dead || health.CurrentHealth <= 0)
            return;

        var compounds = entity.Get<CompoundStorage>().Compounds;
        ref var atpBudget = ref entity.Get<AtpBudget>();

        HandleHitpointsRegeneration(ref health, compounds, delta);

        HandleOsmoregulationDamage(in entity, ref status, ref health, ref cellProperties, compounds, delta);

        HandleHydrogenSulfideDamage(in entity, compounds, ref health, ref cellProperties);

        // Reset amounts in atp budget so it is clean for the next frame.
        atpBudget.Reset();

        // There used to be the engulfing mode ATP handling here, but it is now in EngulfingSystem as it makes more
        // sense to be in there
    }

    private void HandleHydrogenSulfideDamage(in Entity entity, CompoundBag compounds, ref Health health,
        ref CellProperties cellProperties)
    {
        if (hydrogenSulfideDamageTrigger
            && compounds.GetCompoundAmount(Compound.Hydrogensulfide) > Constants.HYDROGEN_SULFIDE_DAMAGE_THESHOLD
            && !entity.Get<OrganelleContainer>().HydrogenSulfideProtection)
        {
            compounds.TakeCompound(Compound.Hydrogensulfide, Constants.HYDROGEN_SULFIDE_DAMAGE_COMPOUND_DRAIN);

            health.DealMicrobeDamage(ref cellProperties, Constants.HYDROGEN_SULFIDE_DAMAGE, "hydrogenSulfide",
                HealthHelpers.GetInstantKillProtectionThreshold(entity));

            entity.SendNoticeIfPossible(() =>
                new SimpleHUDMessage(Localization.Translate("NOTICE_HYDROGEN_SULFIDE_DAMAGE"), DisplayDuration.Short));
        }
    }

    private void HandleOsmoregulationDamage(in Entity entity, ref MicrobeStatus status, ref Health health,
        ref CellProperties cellProperties, CompoundBag compounds, float delta)
    {
        status.LastCheckedATPDamage += delta;

        // TODO: should this loop be made into a single if to ensure that ATP damage can't stack a lot if the game
        // lags?
        while (status.LastCheckedATPDamage >= Constants.ATP_DAMAGE_CHECK_INTERVAL)
        {
            status.LastCheckedATPDamage -= Constants.ATP_DAMAGE_CHECK_INTERVAL;

            // When engulfed osmoregulation cost is not taken
            if (entity.Get<Engulfable>().PhagocytosisStep != PhagocytosisPhase.None)
                return;

            ApplyATPDamage(compounds, ref health, ref cellProperties, entity);
        }
    }

    /// <summary>
    ///     Damage the microbe if it's too low on ATP.
    /// </summary>
    private void ApplyATPDamage(CompoundBag compounds, ref Health health, ref CellProperties cellProperties,
        in Entity entity)
    {
        // TODO: Should this be based on atpBudget.IsMandatoryAtpRequirementCovered?
        if (compounds.GetCompoundAmount(Compound.ATP) > Constants.ATP_DAMAGE_THRESHOLD)
            return;

        health.DealMicrobeDamage(ref cellProperties, health.MaxHealth * Constants.NO_ATP_DAMAGE_FRACTION,
            "atpDamage", HealthHelpers.GetInstantKillProtectionThreshold(entity));
    }

    /// <summary>
    ///     Regenerate hitpoints while the cell has atp
    /// </summary>
    private void HandleHitpointsRegeneration(ref Health health, CompoundBag compounds, float delta)
    {
        if (health.HealthRegenCooldown > 0)
        {
            health.HealthRegenCooldown -= delta;
        }
        else
        {
            if (health.CurrentHealth >= health.MaxHealth)
                return;

            // TODO: Should this be based on atpBudget.IsMandatoryAtpRequirementCovered?
            var atpAmount = compounds.GetCompoundAmount(Compound.ATP);
            if (atpAmount < Constants.HEALTH_REGENERATION_ATP_THRESHOLD && atpAmount / compounds.GetCapacityForCompound(Compound.ATP) <
                Constants.HEALTH_REGENERATION_ALTERNATIVE_ATP_FRACTION)
            {
                // Allow small cells to heal if they are almost full on ATP
                return;
            }

            health.CurrentHealth += Constants.HEALTH_REGENERATION_RATE * delta;
            if (health.CurrentHealth > health.MaxHealth)
            {
                health.CurrentHealth = health.MaxHealth;
            }
        }
    }
}
