# Big City Performance

A Cities: Skylines II code mod to keep the simulation running at speed as a city grows past 100,000 people.

**Work in progress: nothing is released yet.**

## Why big cities slow down

The frame rate usually holds up. It's game time that crawls. Every citizen trip needs a route, and the game gives each batch
of route requests a deadline. When the route-finder falls behind, the simulation clock slows down and waits for it. Bigger
cities mean more trips and more waiting.

## The plan

1. Measure: a repeatable benchmark city, so every change is judged by numbers.
2. Stop the clock waiting on route requests when the route-finder is behind.
3. A route cache, so the same trip isn't searched again and again.
4. Less per-citizen work for things that change slowly.
5. An optional scale setting where one simulated person stands for several, shown openly in the game.

Each step ships only if it measurably helps.

## Licence

GPL-3.0. See [LICENSE](LICENSE) and [CREDITS.md](CREDITS.md).
