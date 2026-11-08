// The count of people simulated in detail, inside the game's own population box at the bottom of the screen: a divider like
// the weather box's, the game's people icon, "48k" and AUTO when the limit is automatic. Hovering it shows the details,
// clicking it opens the mod's settings. The vanilla PopulationField is extended; our part is rendered into the box's own element
// (a portal, re-attached if the game redraws it), so it sits inside the box rather than beside it.
// Adapted from Placemaker (jackwrichards, GPL-3.0) by way of Footbridge Builder's toolbar button. Modified by TevBenji, 2026-11:
// a read-only segment in the population box, fed by one JSON binding.
import React, { useLayoutEffect, useRef, useState } from "react";
import { createPortal, findDOMNode } from "react-dom";
import { getModule, ModuleRegistryExtend } from "cs2/modding";
import { bindValue, trigger, useValue } from "cs2/api";
import * as ui from "cs2/ui";
import { ErrorBoundary } from "./errorBoundary";
import styles from "./PeopleInDetail.module.css";

export const POPULATION_FIELD = "game-ui/game/components/toolbar/bottom/population-field/population-field.tsx";
const POPULATION_CLASSES = "game-ui/game/components/toolbar/bottom/population-field/population-field.module.scss";
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

function populationClass(): string {
    try { return ((getModule(POPULATION_CLASSES, "classes") ?? {}).populationField ?? "").split(" ")[0]; }
    catch { return ""; }
}

/** The population box itself: the element carrying the game's populationField class. */
function findBox(root: Element): HTMLElement | null {
    const name = populationClass();
    if (!name) return null;
    if (root.classList.contains(name)) return root as HTMLElement;
    const inside = root.querySelector("." + name);
    return inside instanceof HTMLElement ? inside : null;
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
    const segment = <div className={styles.segment} onClick={() => trigger(GROUP, "openOptions")}>
        <div className={styles.divider} />
        <img className={styles.icon} src="Media/Game/Icons/Population.svg" />
        <span className={styles.count}>{data.count}</span>
        {data.auto && <span className={styles.chip}>AUTO</span>}
    </div>;
    return ui.Tooltip ? <ui.Tooltip direction="up" delayTime={250} tooltip={details}>{segment}</ui.Tooltip> : segment;
}

// ponytail: findDOMNode targets the game's React 18 runtime; use a forwarded host ref when the game exposes one or upgrades React.
class FieldHost extends React.Component<{ component: React.ComponentType<any>; componentProps: any }> {
    render() { const Component = this.props.component; return <Component {...this.props.componentProps} />; }
}

function Slot({ host }: { host: React.RefObject<FieldHost> }) {
    const own = useRef<HTMLDivElement | null>(null);
    const [slot, setSlot] = useState<HTMLElement | null>(null);
    useLayoutEffect(() => {
        let observer: MutationObserver | undefined;
        try {
            const root = findDOMNode(host.current);
            if (!(root instanceof HTMLElement)) return;
            const attach = () => {
                try {
                    const current = findDOMNode(host.current);
                    if (!(current instanceof HTMLElement)) return;
                    const box = findBox(current);
                    if (!box) return;
                    if (!own.current) {
                        own.current = document.createElement("div");
                        own.current.style.display = "flex";
                    }
                    if (own.current.parentElement !== box) box.appendChild(own.current);
                    setSlot(previous => previous === own.current ? previous : own.current);
                } catch (error) { console.error("[BigCityPerformance] Population box slot failed", error); }
            };
            attach();
            observer = new MutationObserver(attach);
            observer.observe(root.parentElement ?? root, { childList: true, subtree: true });
        } catch (error) { console.error("[BigCityPerformance] Population box attachment failed", error); }
        return () => observer?.disconnect();
    });
    useLayoutEffect(() => () => own.current?.remove(), []);
    return slot ? createPortal(<Segment />, slot) : null;
}

export const withPeopleInDetail: ModuleRegistryExtend = Component => props => {
    const host = useRef<FieldHost>(null);
    return <><FieldHost ref={host} component={Component} componentProps={props ?? {}} /><ErrorBoundary name="population box"><Slot host={host} /></ErrorBoundary></>;
};
