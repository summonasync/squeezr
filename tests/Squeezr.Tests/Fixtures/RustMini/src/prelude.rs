pub mod model;

pub trait Named {
    fn name(&self) -> String;
}

pub fn spare() -> u32 {
    BASE + model::LIMIT
}

const BASE: u32 = 1;
