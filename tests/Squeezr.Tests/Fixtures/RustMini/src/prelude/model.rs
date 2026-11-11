pub struct Tagged {
    pub label: String,
}

impl Tagged {
    pub fn nothing() -> u32 {
        super::spare()
    }

    pub fn take(&self) -> String {
        String::from(self.label.clone())
    }
}

pub trait Shape {
    fn sides(&self) -> u32;
}

pub const LIMIT: u32 = 8;

type Alias = u32;
