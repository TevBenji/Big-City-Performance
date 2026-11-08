// Big City Performance UI entry. The one hook-up is in its own try/catch: a game update renaming the population box must not
// break the rest of the game's interface. Registration pattern adapted from Placemaker (jackwrichards, GPL-3.0) by way of
// Footbridge Builder. Modified by TevBenji, 2026-11: only the population box extension.
import { ModRegistrar } from "cs2/modding";
import { POPULATION_FIELD, withPeopleInDetail } from "./PeopleInDetail";

const register: ModRegistrar = registry => {
    try { registry.extend(POPULATION_FIELD, "PopulationField", withPeopleInDetail); }
    catch (error) { console.error("[BigCityPerformance] Population box extension failed", error); }
};
export default register;
