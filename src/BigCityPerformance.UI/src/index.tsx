// Big City Performance UI entry. The one hook-up is in its own try/catch: a game update changing the interface must not break
// the rest of it. Registration pattern adapted from Placemaker (jackwrichards, GPL-3.0) by way of Footbridge Builder. Modified
// by TevBenji, 2026-11: only the count in the population box.
import { ModRegistrar } from "cs2/modding";
import { PopulationBox } from "./PeopleInDetail";

const register: ModRegistrar = registry => {
    try { registry.append("Game", PopulationBox); }
    catch (error) { console.error("[BigCityPerformance] Population box registration failed", error); }
};
export default register;
