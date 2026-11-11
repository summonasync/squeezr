pub mod engine;
pub mod prelude;
pub mod registry;

use crate::prelude::model::*;
use registry::Summary;

macro_rules! note {
    ($msg:literal) => {
        println!("{}", $msg);
    };
}

pub fn boot(title: &str) -> String {
    let registry = registry::Registry {
        entries: std::collections::HashMap::new(),
    };
    registry.render()
}

pub fn build(count: u32) -> u32 {
    let twice = |value: u32| value * 2;
    twice(engine::audit(count))
}
