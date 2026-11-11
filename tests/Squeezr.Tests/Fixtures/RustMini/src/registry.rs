use std::collections::HashMap;

use crate::prelude::model::{Shape as Shaped, Tagged};
use crate::prelude::{spare, Named};
use crate::registry::things::*;
use things::*;

pub struct Registry {
    pub entries: HashMap<String, String>,
}

pub trait Summary {
    fn render(&self) -> String;
}

impl Registry {
    pub fn new(capacity: usize) -> Registry {
        Registry {
            entries: HashMap::with_capacity(capacity),
        }
    }

    pub fn shout(&self) {
        note!("registry");
    }
}

impl Summary for Registry {
    fn render(&self) -> String {
        let label = |entry: &str| String::from(entry);
        self.entries.keys().count().to_string()
    }
}

impl Shaped for Registry {
    fn sides(&self) -> u32 {
        spare()
    }
}

impl Named for &Registry {
    fn name(&self) -> String {
        self.shout();
        String::new()
    }
}

impl<T> app::Holder<T> {
    pub fn take(&self) {
        Tagged::nothing();
    }
}

impl<T> serde::Serialize<T> for Registry {
    fn dump(&self) -> String {
        String::new()
    }
}

mod things {
    pub struct Ledger {
        pub closed: bool,
    }
}
