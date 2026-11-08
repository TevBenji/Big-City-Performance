// The count of people simulated in detail, inside the game's own population box at the bottom of the screen: a divider like
// the weather box's, the game's people icon, "48k" and AUTO when the limit is automatic. Hovering it shows the details,
// clicking it opens the mod's settings. Our part is rendered into the box's own element (a portal, re-attached if the game
// redraws it), so it sits inside the box rather than beside it.
// Adapted from Placemaker (jackwrichards, GPL-3.0) by way of Footbridge Builder's toolbar button. Modified by TevBenji, 2026-11:
// a read-only segment in the population box, fed by one JSON binding.
import React, { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { getModule } from "cs2/modding";
import { bindValue, trigger, useValue } from "cs2/api";
import * as ui from "cs2/ui";
import { ErrorBoundary } from "./errorBoundary";
import styles from "./PeopleInDetail.module.css";

const POPULATION_CLASSES = "game-ui/game/components/toolbar/bottom/population-field/population-field.module.scss";
const STAT_FIELD_CLASSES = "game-ui/game/components/toolbar/components/stat-field/stat-field.module.scss";
const GROUP = "BigCityPerformance";
const indicator = bindValue<string>(GROUP, "indicator", "{}");

interface Indicator {
    show?: boolean;
    count?: string;
    auto?: boolean;
    title?: string;
    rows?: [string, string][];
    footer?: string;
    hint?: string;
}

/** Never throws: a broken value just hides the segment. */
function read(json: string): Indicator {
    try {
        const value = JSON.parse(json);
        return value && typeof value === "object" ? value : {};
    } catch { return {}; }
}

function firstClass(module: string, key: string): string {
    try { return ((getModule(module, "classes") ?? {})[key] ?? "").split(" ")[0]; }
    catch { return ""; }
}

/** The row inside the population box that holds its icon, number and trend arrow; "" if a game update renamed them. */
function populationRow(): string {
    const box = firstClass(POPULATION_CLASSES, "container"), row = firstClass(STAT_FIELD_CLASSES, "content");
    return box && row ? `.${box} .${row}` : "";
}

function Segment() {
    const data = read(useValue(indicator));
    if (!data.show) return null;
    const details = <div className={styles.panel}>
        <div className={styles.title}><div className={styles.bar} />{data.title}</div>
        {(data.rows ?? []).map(([label, value]) => <div key={label} className={styles.row}>
            <span className={styles.label}>{label}</span><span className={styles.value}>{value}</span>
        </div>)}
        {data.footer && <div className={styles.footer}>{data.footer}</div>}
        {data.hint && <div className={styles.hint}>{data.hint}</div>}
    </div>;
    const segment = <div className={styles.segment} onClick={event => { event.stopPropagation(); trigger(GROUP, "openOptions"); }}>
        <div className={styles.divider} />
        <img className={styles.icon} src="Media/Game/Icons/Population.svg" />
        <span className={styles.count}>{data.count}</span>
        {data.auto && <span className={styles.chip}>AUTO</span>}
    </div>;
    return ui.Tooltip ? <ui.Tooltip direction="up" delayTime={250} tooltip={details}>{segment}</ui.Tooltip> : segment;
}

/**
 * Rendered in the game's "Game" layer, it puts the segment into the population box: the game's PopulationField export can't be
 * extended (its setter assigns a constant: "Assignment to constant variable", test run 8 November 2026), so the box is found by
 * its class and the segment goes in as a portal. Once a second it checks the segment is still there, in case the bar was redrawn.
 */
export function PopulationBox() {
    const own = useRef<HTMLDivElement | null>(null);
    const [slot, setSlot] = useState<HTMLElement | null>(null);
    useEffect(() => {
        const attach = () => {
            try {
                const selector = populationRow();
                if (!selector) return;
                const box = document.querySelector(selector);
                if (!(box instanceof HTMLElement)) return;
                if (!own.current) {
                    own.current = document.createElement("div");
                    own.current.style.display = "flex";
                    own.current.style.flexShrink = "0";  // the game's number shrinks to fit, not our count
                }
                if (own.current.parentElement !== box) box.appendChild(own.current);
                setSlot(previous => previous === own.current ? previous : own.current);
            } catch (error) { console.error("[BigCityPerformance] Population box slot failed", error); }
        };
        attach();
        const timer = setInterval(attach, 1000);
        return () => {
            clearInterval(timer);
            own.current?.remove();
        };
    }, []);
    return slot ? <ErrorBoundary name="population box">{createPortal(<Segment />, slot)}</ErrorBoundary> : null;
}
