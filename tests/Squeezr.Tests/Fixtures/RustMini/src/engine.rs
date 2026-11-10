pub mod pipeline {
    pub mod stage {
        pub fn run(registry: &crate::registry::Registry) -> String {
            registry.render()
        }
    }
}

pub mod holder {
    pub struct Holder {
        pub label: String,
    }

    impl Holder {
        pub fn take(&self) -> String {
            String::from(self.label.clone())
        }
    }
}

pub fn audit(steps: u32) -> u32 {
    let mut total = steps;
    total += 1;
    total
}
